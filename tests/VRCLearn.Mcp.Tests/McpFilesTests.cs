using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using VRCLearn.App.Mcp;
using Xunit;

namespace VRCLearn.Mcp.Tests;

public sealed class McpFilesTests
{
    [Fact]
    public void SingleFileSelectionPreservesChinesePathsAndMetadata()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("我的 avatar.unitypackage");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            var result = Request(files, "file", pending =>
            {
                Assert.Equal("选择中文文件", pending.Title);
                Assert.Equal("Unity package|*.unitypackage", pending.Filter);
                Assert.Equal(directory.Path, pending.InitialPath);
                Assert.True(Guid.TryParseExact(pending.Id, "N", out _));
                files.Answer(pending.Id, [System.IO.Path.GetRelativePath(Environment.CurrentDirectory, path)]);
            }, "Unity package|*.unitypackage", directory.Path);

            Assert.Equal(path, Assert.Single(Assert.IsType<string[]>(result)));
            Assert.Null(files.Pending);
        });
    }

    [Fact]
    public void MultipleFilesAndFolderSelectionsUseTheirNativeResultShapes()
    {
        using var directory = new TemporaryDirectory();
        var first = directory.File("项目一.json");
        var second = directory.File("项目二.json");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            var multiple = Request(files, "files", pending => files.Answer(pending.Id, [first, second]));
            var folder = Request(files, "folder", pending => files.Answer(pending.Id, [directory.Path]));

            Assert.Equal(new[] { first, second }, Assert.IsType<string[]>(multiple));
            Assert.Equal(directory.Path, Assert.Single(Assert.IsType<string[]>(folder)));
            Assert.Null(files.Pending);
        });
    }

    [Fact]
    public void CancelReturnsNullAndEndsTheRequest()
    {
        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            string? id = null;
            var result = Request(files, "file", pending =>
            {
                id = pending.Id;
                files.Answer(pending.Id, null);
            });

            Assert.Null(result);
            Assert.Null(files.Pending);
            Assert.Throws<ArgumentException>(() => files.Answer(id!, null));
        });
    }

    [Fact]
    public void UnknownRequestIdsCannotAnswerOrReplaceAnActiveRequest()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("有效文件.txt");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            Assert.Throws<ArgumentException>(() => files.Answer("missing", [path]));

            var result = Request(files, "file", pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer("wrong-id", [path]));
                Assert.Same(pending, files.Pending);
                files.Answer(pending.Id, [path]);
            });

            Assert.Equal(path, Assert.Single(Assert.IsType<string[]>(result)));
        });
    }

    [Theory]
    [InlineData("file", 0)]
    [InlineData("file", 2)]
    [InlineData("folder", 2)]
    [InlineData("save", 2)]
    [InlineData("files", 0)]
    public void InvalidSelectionCountsLeaveTheRequestPending(string kind, int count)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("文件.txt");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            var result = Request(files, kind, pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, Enumerable.Repeat(path, count).ToArray()));
                Assert.Same(pending, files.Pending);
                files.Answer(pending.Id, null);
            });

            Assert.Null(result);
            Assert.Null(files.Pending);
        });
    }

    [Fact]
    public void MissingAndWrongKindPathsAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("真实文件.txt");
        var missing = System.IO.Path.Combine(directory.Path, "不存在.txt");
        var missingParent = System.IO.Path.Combine(directory.Path, "不存在目录", "保存.txt");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            Request(files, "file", pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [missing]));
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [directory.Path]));
                files.Answer(pending.Id, null);
            });
            Request(files, "folder", pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [path]));
                files.Answer(pending.Id, null);
            });
            Request(files, "save", pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [missingParent]));
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [directory.Path]));
                files.Answer(pending.Id, null);
            });

            Assert.Null(files.Pending);
        });
    }

    [Fact]
    public void SavingAnExistingFileRequiresExplicitOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("已有备份.zip");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            var result = Request(files, "save", pending =>
            {
                Assert.Throws<ArgumentException>(() => files.Answer(pending.Id, [path]));
                Assert.Same(pending, files.Pending);
                files.Answer(pending.Id, [path], overwrite: true);
            });

            Assert.Equal(path, Assert.Single(Assert.IsType<string[]>(result)));
            Assert.Equal("测试内容", File.ReadAllText(path));
        });
    }

    [Fact]
    public void SavingANewFileOnlySelectsThePath()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "新备份.zip");

        RunSta(() =>
        {
            var files = new McpFiles(CancellationToken.None);
            var result = Request(files, "save", pending => files.Answer(pending.Id, [path]));

            Assert.Equal(path, Assert.Single(Assert.IsType<string[]>(result)));
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void CancellationBeforeRequestDoesNotPublishPendingState()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        RunSta(() =>
        {
            var files = new McpFiles(cancellation.Token);

            Assert.Throws<OperationCanceledException>(() => files.Request("file", "标题"));
            Assert.Null(files.Pending);
        });
    }

    [Fact]
    public void CancellationWhileWaitingUnblocksTheDispatcherFrame()
    {
        using var cancellation = new CancellationTokenSource();

        RunSta(() =>
        {
            var files = new McpFiles(cancellation.Token);

            Assert.Throws<OperationCanceledException>(() => Request(files, "file", _ => cancellation.Cancel()));
            Assert.Null(files.Pending);
        });
    }

    private static string[]? Request(
        McpFiles files,
        string kind,
        Action<FileRequest> respond,
        string filter = "",
        string initialPath = "")
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                respond(Assert.IsType<FileRequest>(files.Pending));
            }
            catch (Exception exception)
            {
                failure = exception;
                if (files.Pending is { } pending)
                {
                    files.Answer(pending.Id, null);
                }
            }
        }));

        var result = files.Request(kind, "选择中文文件", filter, initialPath);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "文件选择未在 10 秒内结束。");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"VRCLearn.Mcp.Tests-{Guid.NewGuid():N}", "中文 路径");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, "测试内容");
            return path;
        }

        public void Dispose()
        {
            var root = Directory.GetParent(Path)!.FullName;
            var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(root).StartsWith("VRCLearn.Mcp.Tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("测试目录超出临时路径。");
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
