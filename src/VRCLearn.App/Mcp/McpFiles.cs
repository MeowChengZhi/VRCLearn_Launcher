using System.IO;
using System.Windows.Threading;

namespace VRCLearn.App.Mcp;

/// <summary>MCP 操作的文件选择仍由 DialogService 发起, 普通界面保留系统选择框。</summary>
public sealed class McpFiles
{
    public static AsyncLocal<McpFiles?> Current { get; } = new();

    private readonly CancellationToken _cancellationToken;
    private DispatcherFrame? _frame;
    private string[]? _answer;
    public FileRequest? Pending { get; private set; }

    public McpFiles(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;

    public string[]? Request(string kind, string title, string filter = "", string initialPath = "")
    {
        _cancellationToken.ThrowIfCancellationRequested();
        Pending = new FileRequest(Guid.NewGuid().ToString("N"), kind, title, filter, initialPath);
        _answer = null;
        _frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var registration = _cancellationToken.Register(() => dispatcher.BeginInvoke(() => Cancel()));

        try
        {
            Dispatcher.PushFrame(_frame);
            _cancellationToken.ThrowIfCancellationRequested();
            return _answer;
        }
        finally
        {
            Pending = null;
            _frame = null;
        }
    }

    public void Answer(string id, string[]? paths, bool overwrite = false)
    {
        if (Pending is not { } request || request.Id != id || _frame is null)
        {
            throw new ArgumentException("文件选择请求已结束或不存在。");
        }

        if (paths is null)
        {
            Cancel();
            return;
        }

        if (paths.Length == 0 || (request.Kind != "files" && paths.Length != 1))
        {
            throw new ArgumentException("文件选择数量不正确。");
        }

        var fullPaths = paths.Select(Path.GetFullPath).ToArray();
        foreach (var path in fullPaths)
        {
            var exists = request.Kind switch
            {
                "folder" => Directory.Exists(path),
                "save" => Directory.Exists(Path.GetDirectoryName(path)) && !Directory.Exists(path),
                _ => File.Exists(path),
            };
            if (!exists)
            {
                throw new ArgumentException($"所选路径不存在: {path}");
            }
            if (request.Kind == "save" && File.Exists(path) && !overwrite)
            {
                throw new ArgumentException("目标文件已存在, 确认覆盖后设置 overwrite=true。");
            }
        }

        _answer = fullPaths;
        Pending = null;
        _frame.Continue = false;
    }

    private void Cancel()
    {
        _answer = null;
        Pending = null;
        if (_frame is { } frame)
        {
            frame.Continue = false;
        }
    }
}

public sealed record FileRequest(string Id, string Kind, string Title, string Filter, string InitialPath);
