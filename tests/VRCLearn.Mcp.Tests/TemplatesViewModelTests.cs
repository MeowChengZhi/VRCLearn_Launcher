using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VRCLearn.App;
using VRCLearn.App.Services;
using VRCLearn.App.ViewModels;
using VRCLearn.Core.Configuration;
using VRCLearn.Core.IO;
using VRCLearn.Core.Templates;
using VRCLearn.Core.Unity;
using VRCLearn.Infrastructure.Http;
using Xunit;

namespace VRCLearn.Mcp.Tests;

public sealed class TemplatesViewModelTests
{
    [Fact]
    public async Task EditingAnUnavailableTemplateUsesItsOriginalName()
    {
        await RunSta(async () =>
        {
            var definition = Template("missing-base", "原始中文模板名", baseId: "not-installed-base");
            using var fixture = new TestFixture(definition);
            var viewModel = fixture.ViewModel;
            await viewModel.ReloadAsync();
            var option = Assert.Single(viewModel.Templates, template => template.Id == definition.Id);

            Assert.False(option.IsAvailable);
            Assert.Contains(definition.DisplayName, option.Display);
            Assert.NotEqual(definition.DisplayName, option.Display);

            await viewModel.EditTemplateCommand.ExecuteAsync(option);

            Assert.True(viewModel.IsEditing);
            Assert.Equal(definition.DisplayName, viewModel.EditorName);
            Assert.NotEqual(option.Display, viewModel.EditorName);
        });
    }

    [Fact]
    public async Task SavingAnExistingTemplatePreservesItsFilesDirectory()
    {
        await RunSta(async () =>
        {
            const string files = "本地文件/中文 配置";
            var definition = Template("local-template", "配置模板", files);
            using var fixture = new TestFixture(definition);
            var viewModel = fixture.ViewModel;
            await viewModel.ReloadAsync();
            var option = Assert.Single(viewModel.Templates, template => template.Id == definition.Id);

            await viewModel.EditTemplateCommand.ExecuteAsync(option);
            viewModel.EditorName = "保存后的中文模板名";
            await viewModel.SaveTemplateCommand.ExecuteAsync(null);

            var saved = Assert.Single(fixture.Store.Saved);
            Assert.Equal(definition.Id, saved.Id);
            Assert.Equal("保存后的中文模板名", saved.DisplayName);
            Assert.Equal(files, saved.Files);
            Assert.False(viewModel.IsEditing);
            var reloaded = Assert.Single(viewModel.Templates, template => template.Id == definition.Id);
            Assert.Equal(files, Assert.IsType<AlcomTemplate>(reloaded.Definition).Files);
            var plan = Assert.IsType<TemplatePlan>(viewModel.PlanFor(reloaded.Id, UnityReleaseCatalog.StableVersion));
            var source = Assert.IsAssignableFrom<IFileIo>(plan.Source);
            Assert.Equal(Path.GetFullPath(Path.Combine(fixture.TemplateDirectory, files)), source.RootDirectory);
        });
    }

    [Fact]
    public async Task CreatingANewTemplateDoesNotReuseThePreviouslyEditedTemplatesIdOrFiles()
    {
        await RunSta(async () =>
        {
            var existing = Template("existing-template", "同名模板", "旧模板文件");
            using var fixture = new TestFixture(existing);
            var viewModel = fixture.ViewModel;
            await viewModel.ReloadAsync();
            var option = Assert.Single(viewModel.Templates, template => template.Id == existing.Id);

            await viewModel.EditTemplateCommand.ExecuteAsync(option);
            viewModel.CancelEditCommand.Execute(null);
            await viewModel.NewTemplateCommand.ExecuteAsync(null);
            viewModel.EditorName = existing.DisplayName;
            viewModel.EditorBase = Assert.Single(viewModel.Bases, template => template.Id == TemplateFormat.BlankId);
            await viewModel.SaveTemplateCommand.ExecuteAsync(null);

            var saved = Assert.Single(fixture.Store.Saved);
            Assert.False(string.IsNullOrWhiteSpace(saved.Id));
            Assert.NotEqual(existing.Id, saved.Id);
            Assert.Equal(existing.DisplayName, saved.DisplayName);
            Assert.Equal(TemplateFormat.BlankId, saved.Base);
            Assert.Null(saved.Files);
            Assert.False(viewModel.IsEditing);
            var original = Assert.Single(fixture.Store.Templates, template => template.Template.Id == existing.Id);
            Assert.Same(existing, original.Template);
            var added = Assert.Single(viewModel.Templates, template => template.Id == saved.Id);
            Assert.Null(Assert.IsType<AlcomTemplate>(added.Definition).Files);
        });
    }

    [Fact]
    public async Task EditingTemplatesWithTheSameNameKeepsFilesAttachedToTheirOwnIds()
    {
        await RunSta(async () =>
        {
            var first = Template("template-first", "相同模板名", "first_files");
            var second = Template("template-second", "相同模板名", "second_files");
            using var fixture = new TestFixture(first, second);
            var viewModel = fixture.ViewModel;
            await viewModel.ReloadAsync();

            var firstOption = Assert.Single(viewModel.Templates, template => template.Id == first.Id);
            var secondOption = Assert.Single(viewModel.Templates, template => template.Id == second.Id);
            Assert.Equal(firstOption.Display, secondOption.Display);
            await viewModel.EditTemplateCommand.ExecuteAsync(firstOption);
            viewModel.CancelEditCommand.Execute(null);
            await viewModel.EditTemplateCommand.ExecuteAsync(secondOption);
            await viewModel.SaveTemplateCommand.ExecuteAsync(null);

            firstOption = Assert.Single(viewModel.Templates, template => template.Id == first.Id);
            await viewModel.EditTemplateCommand.ExecuteAsync(firstOption);
            await viewModel.SaveTemplateCommand.ExecuteAsync(null);

            Assert.Collection(fixture.Store.Saved,
                saved =>
                {
                    Assert.Equal(second.Id, saved.Id);
                    Assert.Equal(second.DisplayName, saved.DisplayName);
                    Assert.Equal(second.Files, saved.Files);
                },
                saved =>
                {
                    Assert.Equal(first.Id, saved.Id);
                    Assert.Equal(first.DisplayName, saved.DisplayName);
                    Assert.Equal(first.Files, saved.Files);
                });
            Assert.Equal(first.Files, Assert.Single(fixture.Store.Templates, template => template.Template.Id == first.Id).Template.Files);
            Assert.Equal(second.Files, Assert.Single(fixture.Store.Templates, template => template.Template.Id == second.Id).Template.Files);
        });
    }

    private static AlcomTemplate Template(string id, string name, string? files = null, string baseId = TemplateFormat.BlankId) =>
        new(name, id, baseId, null, new Dictionary<string, string>(), [], null, files);

    private static Task RunSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await test();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;

        public TestFixture(params AlcomTemplate[] templates)
        {
            _root = Path.Combine(Path.GetTempPath(), $"VRCLearn.TemplateTests-{Guid.NewGuid():N}");
            var paths = new AppPaths(_root);
            TemplateDirectory = Path.Combine(paths.PrivateDirectory, "templates");
            Directory.CreateDirectory(TemplateDirectory);
            Store = new TestTemplateStore(TemplateDirectory, templates);
            foreach (var template in templates)
            {
                if (template.Files is not { Length: > 0 } files)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.Combine(TemplateDirectory, files));
            }

            var services = new ServiceCollection();
            services.AddLogging();
            AppServices.AddLauncher(services, paths, new MutableProxy());
            services.AddSingleton<IUserTemplateStore>(Store);
            services.AddSingleton<IUnityEditorLocator>(new TestEditors(_root));
            _services = services.BuildServiceProvider();
            _services.GetRequiredService<LocalizationService>().Apply("zh-Hans");
            ViewModel = _services.GetRequiredService<TemplatesViewModel>();
        }

        public string TemplateDirectory { get; }

        public TestTemplateStore Store { get; }

        public TemplatesViewModel ViewModel { get; }

        public void Dispose()
        {
            _services.Dispose();
            var fullRoot = Path.GetFullPath(_root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("VRCLearn.TemplateTests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("测试目录超出临时路径。");
            }

            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private sealed class TestTemplateStore : IUserTemplateStore
    {
        private readonly string _directory;

        public TestTemplateStore(string directory, IReadOnlyList<AlcomTemplate> templates)
        {
            _directory = directory;
            Templates = [.. templates.Select(template =>
                new AlcomTemplateFile(Path.Combine(directory, template.Id + TemplateFormat.Extension), template))];
        }

        public List<AlcomTemplateFile> Templates { get; }

        public List<AlcomTemplate> Saved { get; } = [];

        public Task<UserTemplates> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UserTemplates([.. Templates]));
        }

        public Task SaveAsync(AlcomTemplate template, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(template);
            var index = Templates.FindIndex(existing => existing.Template.Id == template.Id);
            if (index >= 0)
            {
                Templates[index] = Templates[index] with { Template = template };
                return Task.CompletedTask;
            }

            Templates.Add(new AlcomTemplateFile(Path.Combine(_directory, template.Id + TemplateFormat.Extension), template));
            return Task.CompletedTask;
        }

        public bool Remove(string path) => throw new NotSupportedException();

        public Task<TemplateImportResult> ImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> OverrideAsync(IReadOnlyList<TemplateImportDuplicate> duplicates, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestEditors(string root) : IUnityEditorLocator
    {
        private readonly UnityEditor _editor = new(UnityReleaseCatalog.StableVersion, Path.Combine(root, "Unity.exe"), null);

        public IReadOnlyList<UnityEditor> FindEditors() => [_editor];

        public Task RefreshFromHubAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public UnityEditor? Probe(string executablePath) => _editor.ExecutablePath == executablePath ? _editor : null;
    }
}
