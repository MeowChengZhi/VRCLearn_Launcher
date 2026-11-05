using System.Collections;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using VRCLearn.App.Services;
using VRCLearn.App.ViewModels;

namespace VRCLearn.App.Mcp;

public sealed class McpRuntime(IServiceProvider services, Dispatcher dispatcher)
{
    private readonly McpObjects _objects = new();
    private readonly Dictionary<string, Operation> _operations = [];
    private Operation? _active;

    private static readonly IReadOnlyDictionary<string, Type> Pages = new Dictionary<string, Type>
    {
        ["main"] = typeof(MainWindowViewModel),
        ["projects"] = typeof(ProjectsViewModel),
        ["project-detail"] = typeof(ProjectDetailViewModel),
        ["repositories"] = typeof(RepositoriesViewModel),
        ["templates"] = typeof(TemplatesViewModel),
        ["resources"] = typeof(ResourcesViewModel),
        ["backups"] = typeof(BackupsViewModel),
        ["plugins"] = typeof(PluginsViewModel),
        ["settings"] = typeof(SettingsViewModel),
        ["shared-settings"] = typeof(SharedSettingsViewModel),
        ["package-cache"] = typeof(PackageCacheViewModel),
        ["user-packages"] = typeof(UserPackagesViewModel),
        ["updates"] = typeof(UpdateViewModel),
        ["logs"] = typeof(LogsViewModel),
        ["setup"] = typeof(SetupViewModel),
    };

    private sealed class Operation(string name, IAsyncRelayCommand? command, CancellationTokenSource cancellation)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public string State { get; set; } = "queued";
        public string? Error { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public DateTimeOffset? FinishedAt { get; set; }
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public McpFiles Files { get; } = new(cancellation.Token);
        public IAsyncRelayCommand? Command { get; } = command;

        public object Snapshot() => new
        {
            Id, Name, State, Error, StartedAt, FinishedAt,
            cancellationRequested = Cancellation.IsCancellationRequested,
            canCancelCommand = Command?.CanBeCanceled == true,
            fileRequest = Files.Pending,
        };
    }

    public Task<object> OnUi(Func<object> action, CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;

    private object Target(string reference) => Pages.TryGetValue(reference, out var type)
        ? services.GetRequiredService(type)
        : _objects.Resolve(reference);

    public object State() => new
    {
        processId = Environment.ProcessId,
        page = services.GetRequiredService<MainWindowViewModel>().CurrentKey,
        targets = Pages.Keys,
        activeOperation = _active?.Snapshot(),
        windows = Windows(),
    };

    public object Inspect(string reference, string? property, int offset, int limit) =>
        _objects.Inspect(Target(reference), property, offset, limit);

    public object Set(string reference, string property, JsonElement value)
    {
        var target = Target(reference);
        if (target is DependencyObject)
        {
            throw new ArgumentException("界面控件请使用 launcher_ui_set。");
        }

        _objects.Set(target, property, value);
        return _objects.Describe(target);
    }

    public object Navigate(string page)
    {
        if (page is not ("projects" or "project-detail" or "repositories" or "templates" or "resources"
            or "backups" or "plugins" or "settings" or "logs" or "setup"))
        {
            throw new ArgumentException($"页面不存在: {page}");
        }

        services.GetRequiredService<NavigationService>().GoTo(page);
        return State();
    }

    public object OpenProject(string path) => Start("open-project", () =>
    {
        services.GetRequiredService<ProjectDetailViewModel>().Open(path);
        services.GetRequiredService<NavigationService>().GoTo("project-detail");
        return Task.CompletedTask;
    });

    public object Import(string page, string[] paths)
    {
        if (!DropImportService.CanAccept(page, paths))
        {
            throw new ArgumentException("该页面不接受所选文件。支持 projects/backups/templates/plugins。");
        }

        return Start($"import:{page}", () => services.GetRequiredService<DropImportService>().ImportAsync(page, paths));
    }

    public object Execute(string reference, string commandName, JsonElement? argument)
    {
        var (command, parameter) = _objects.Command(Target(reference), commandName, argument);
        return Start($"{reference}.{commandName}", () =>
        {
            if (!command.CanExecute(parameter))
            {
                throw new InvalidOperationException("命令当前不可用。");
            }

            if (command is IAsyncRelayCommand asyncCommand)
            {
                return asyncCommand.ExecuteAsync(parameter);
            }

            command.Execute(parameter);
            return Task.CompletedTask;
        }, command as IAsyncRelayCommand);
    }

    private object Start(string name, Func<Task> action, IAsyncRelayCommand? command = null)
    {
        if (_active is not null)
        {
            throw new InvalidOperationException($"已有操作 {_active.Id} 正在进行。先检查状态/对话框或取消操作。");
        }

        var operation = new Operation(name, command, new CancellationTokenSource());
        _operations.Add(operation.Id, operation);
        _active = operation;

        dispatcher.BeginInvoke(async () =>
        {
            operation.State = "running";
            McpFiles.Current.Value = operation.Files;
            try
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                await action();
                operation.State = operation.Error is not null ? "failed"
                    : operation.Cancellation.IsCancellationRequested ? "cancelled" : "completed";
            }
            catch (OperationCanceledException)
            {
                operation.State = "cancelled";
            }
            catch (Exception exception)
            {
                operation.State = "failed";
                operation.Error = exception.GetBaseException().Message;
            }
            finally
            {
                McpFiles.Current.Value = null;
                operation.FinishedAt = DateTimeOffset.Now;
                operation.Cancellation.Dispose();
                _active = null;
            }
        });

        return operation.Snapshot();
    }

    public object OperationState(string id) => OperationFor(id).Snapshot();

    public object Cancel(string id)
    {
        var operation = OperationFor(id);
        if (operation.FinishedAt is not null)
        {
            return operation.Snapshot();
        }

        operation.Cancellation.Cancel();
        if (operation.Command?.CanBeCanceled == true)
        {
            operation.Command.Cancel();
        }
        if (operation.Files.Pending is { } request)
        {
            operation.Files.Answer(request.Id, null);
        }

        return operation.Snapshot();
    }

    public object FileAnswer(string requestId, string[]? paths, bool overwrite)
    {
        if (_active is not { } operation)
        {
            throw new InvalidOperationException("当前没有操作等待文件选择。");
        }

        operation.Files.Answer(requestId, paths, overwrite);
        return operation.Snapshot();
    }

    private Operation OperationFor(string id) => _operations.GetValueOrDefault(id)
        ?? throw new ArgumentException($"操作不存在: {id}");

    public object Windows() => Application.Current.Windows.OfType<Window>()
        .Where(window => window.IsVisible)
        .Select(window => new
        {
            reference = _objects.Reference(window),
            window.Title,
            window.IsEnabled,
            window.IsActive,
            owner = window.Owner is null ? null : _objects.Reference(window.Owner),
            model = _objects.Value(window.DataContext),
        }).ToList();

    public object Ui(string windowReference, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 必须 >= 0, limit 必须为 1..200。");
        }

        var window = _objects.Resolve(windowReference) as Window
            ?? throw new ArgumentException("需要 windows 返回的窗口引用。");
        var controls = Walk(window).Where(element => element.IsVisible && element is
            TextBlock or TextBox or ButtonBase or Selector or RangeBase or ScrollViewer or MenuItem).ToList();

        return new
        {
            window = windowReference, total = controls.Count, offset,
            hasMore = offset + limit < controls.Count,
            controls = controls.Skip(offset).Take(limit).Select(element => new
            {
                reference = _objects.Reference(element),
                kind = element.GetType().Name,
                element.Name,
                element.IsEnabled,
                label = AutomationProperties.GetName(element),
                text = element switch
                {
                    TextBlock block => block.Text,
                    TextBox box => box.Text,
                    ContentControl { Content: string content } => content,
                    HeaderedItemsControl { Header: string header } => header,
                    _ => null,
                },
                value = element switch
                {
                    ToggleButton toggle => (object?)toggle.IsChecked,
                    Selector selector => _objects.Value(selector.SelectedItem),
                    RangeBase range => range.Value,
                    ScrollViewer scroll => scroll.VerticalOffset,
                    _ => null,
                },
                items = element is Selector list ? list.Items.Cast<object?>().Take(200).Select(_objects.Value).ToList() : null,
                model = _objects.Value(element.DataContext),
            }).ToList(),
        };
    }

    public object UiSet(string reference, JsonElement value)
    {
        var target = _objects.Resolve(reference) as FrameworkElement
            ?? throw new ArgumentException("需要 ui 返回的控件引用。");
        if (!target.IsEnabled || !target.IsVisible)
        {
            throw new InvalidOperationException("控件当前不可用。");
        }

        switch (target)
        {
            case TextBox box when !box.IsReadOnly:
                box.SetCurrentValue(TextBox.TextProperty, value.GetString() ?? "");
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                break;
            case ToggleButton toggle:
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty,
                    value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean());
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                break;
            case Selector selector:
                var item = value.ValueKind == JsonValueKind.Number
                    ? selector.Items[value.GetInt32()]
                    : value.ValueKind == JsonValueKind.Null ? null : _objects.Convert(value, typeof(object));
                selector.SetCurrentValue(Selector.SelectedItemProperty, item);
                selector.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
                selector.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();
                break;
            case RangeBase range:
                range.SetCurrentValue(RangeBase.ValueProperty, value.GetDouble());
                range.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
                break;
            case ScrollViewer scroll:
                scroll.ScrollToVerticalOffset(value.GetDouble());
                break;
            default:
                throw new ArgumentException("支持可写文本框、勾选、下拉选择、数值和滚动控件。");
        }

        return new { reference, changed = true };
    }

    public object UiClick(string reference)
    {
        var target = _objects.Resolve(reference) as FrameworkElement
            ?? throw new ArgumentException("需要 ui 返回的按钮引用。");
        if (!target.IsEnabled || !target.IsVisible)
        {
            throw new InvalidOperationException("按钮当前不可用。");
        }

        return StartOrInteract($"click:{reference}", target, () =>
        {
            switch (target)
            {
                case ButtonBase button:
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    return RunCommand(button.Command, button.CommandParameter);
                case MenuItem menu:
                    menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
                    return RunCommand(menu.Command, menu.CommandParameter);
                default:
                    throw new ArgumentException("控件不是按钮或菜单项。");
            }
        });
    }

    // 现有操作等待确认时, 允许响应它的模态窗口, 不启动第二个后台任务。
    private object StartOrInteract(string name, FrameworkElement target, Func<Task> action)
    {
        if (_active is not null)
        {
            if (Window.GetWindow(target)?.Owner is null)
            {
                throw new InvalidOperationException("操作尚未结束, 仅可响应其子窗口。取消请使用 launcher_cancel。");
            }

            var previous = McpFiles.Current.Value;
            McpFiles.Current.Value = _active.Files;
            try
            {
                var interaction = action();
                if (interaction.IsCompleted)
                {
                    interaction.GetAwaiter().GetResult();
                }
                else
                {
                    _ = ObserveInteractionAsync(interaction, _active);
                }
            }
            finally { McpFiles.Current.Value = previous; }
            return State();
        }

        return Start(name, action);
    }

    private static async Task ObserveInteractionAsync(Task task, Operation operation)
    {
        try { await task; }
        catch (Exception exception)
        {
            operation.Error = exception.GetBaseException().Message;
            operation.State = "failed";
        }
    }

    private static Task RunCommand(ICommand? command, object? parameter)
    {
        if (command is null)
        {
            return Task.CompletedTask;
        }
        if (!command.CanExecute(parameter))
        {
            throw new InvalidOperationException("按钮命令当前不可用。");
        }

        if (command is IAsyncRelayCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync(parameter);
        }

        command.Execute(parameter);
        return Task.CompletedTask;
    }

    public object CloseWindow(string reference)
    {
        var window = _objects.Resolve(reference) as Window
            ?? throw new ArgumentException("需要窗口引用。");
        if (window == Application.Current.MainWindow)
        {
            throw new ArgumentException("请在应用界面退出启动器。");
        }

        window.Close();
        return State();
    }

    private static IEnumerable<FrameworkElement> Walk(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            if (node is FrameworkElement element)
            {
                yield return element;
            }
            if (node is not Visual)
            {
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
