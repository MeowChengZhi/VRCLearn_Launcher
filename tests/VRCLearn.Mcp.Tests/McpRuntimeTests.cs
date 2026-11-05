using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using VRCLearn.App.Mcp;
using VRCLearn.App.ViewModels;
using Xunit;

namespace VRCLearn.Mcp.Tests;

public sealed class McpRuntimeTests
{
    [Fact]
    public void OperationsReturnBeforeExecutionAndCompleteWithTheSameId()
    {
        RunSta(dispatcher =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executions = 0;
            var target = new TestCommands(new AsyncRelayCommand(() =>
            {
                executions++;
                return completion.Task;
            }));
            var runtime = new McpRuntime(new TestServices(target), dispatcher);

            var queued = Json(runtime.Execute("projects", nameof(target.WorkCommand), null));
            var id = queued.GetProperty("Id").GetString()!;
            Assert.Equal("queued", queued.GetProperty("State").GetString());
            Assert.Equal(0, executions);
            Assert.Equal(JsonValueKind.Null, queued.GetProperty("FinishedAt").ValueKind);
            Assert.Throws<InvalidOperationException>(() => runtime.Execute("projects", nameof(target.SyncCommand), null));

            PumpUntil(() => State(runtime, id) == "running");
            Assert.Equal(1, executions);
            completion.SetResult();
            PumpUntil(() => State(runtime, id) == "completed");

            var finished = Json(runtime.OperationState(id));
            Assert.Equal(id, finished.GetProperty("Id").GetString());
            Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("FinishedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, finished.GetProperty("Error").ValueKind);
            var afterCancel = Json(runtime.Cancel(id));
            Assert.Equal("completed", afterCancel.GetProperty("State").GetString());
            Assert.False(afterCancel.GetProperty("cancellationRequested").GetBoolean());

            var next = Json(runtime.Execute("projects", nameof(target.SyncCommand), null));
            var nextId = next.GetProperty("Id").GetString()!;
            Assert.NotEqual(id, nextId);
            PumpUntil(() => State(runtime, nextId) == "completed");
            Assert.Equal(1, target.SyncExecutions);
        });
    }

    [Fact]
    public void AsynchronousFailuresBecomeFailedOperationSnapshots()
    {
        RunSta(dispatcher =>
        {
            var target = new TestCommands(new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("中文操作失败"))));
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.WorkCommand), null)).GetProperty("Id").GetString()!;

            PumpUntil(() => State(runtime, id) == "failed");

            var failed = Json(runtime.OperationState(id));
            Assert.Equal("中文操作失败", failed.GetProperty("Error").GetString());
            Assert.NotEqual(JsonValueKind.Null, failed.GetProperty("FinishedAt").ValueKind);
            Assert.Null(McpFiles.Current.Value);
        });
    }

    [Fact]
    public void SynchronousFailuresAreCapturedInsteadOfEscapingTheDispatcher()
    {
        RunSta(dispatcher =>
        {
            var target = new TestCommands(new AsyncRelayCommand(() => Task.CompletedTask))
            {
                SyncCommand = new RelayCommand(() => throw new InvalidOperationException("同步失败")),
            };
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.SyncCommand), null)).GetProperty("Id").GetString()!;

            PumpUntil(() => State(runtime, id) == "failed");

            Assert.Equal("同步失败", Json(runtime.OperationState(id)).GetProperty("Error").GetString());
        });
    }

    [Fact]
    public void CancellingQueuedWorkPreventsCommandExecution()
    {
        RunSta(dispatcher =>
        {
            var target = new TestCommands(new AsyncRelayCommand(() => Task.CompletedTask));
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.SyncCommand), null)).GetProperty("Id").GetString()!;

            runtime.Cancel(id);
            PumpUntil(() => State(runtime, id) == "cancelled");

            Assert.Equal(0, target.SyncExecutions);
            Assert.True(Json(runtime.OperationState(id)).GetProperty("cancellationRequested").GetBoolean());
        });
    }

    [Fact]
    public void CancellingRunningWorkCancelsTheNativeAsyncCommand()
    {
        RunSta(dispatcher =>
        {
            var command = new AsyncRelayCommand(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
            var target = new TestCommands(command);
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.WorkCommand), null)).GetProperty("Id").GetString()!;

            PumpUntil(() => command.IsRunning);
            Assert.True(Json(runtime.OperationState(id)).GetProperty("canCancelCommand").GetBoolean());
            runtime.Cancel(id);
            PumpUntil(() => State(runtime, id) == "cancelled");

            Assert.True(command.IsCancellationRequested);
            Assert.False(command.IsRunning);
            Assert.True(Json(runtime.OperationState(id)).GetProperty("cancellationRequested").GetBoolean());
        });
    }

    [Fact]
    public void CancellingPendingFileSelectionStopsTheOperationBeforeFurtherWork()
    {
        RunSta(dispatcher =>
        {
            var reachedAfterRequest = false;
            var target = new TestCommands(new AsyncRelayCommand(() =>
            {
                var files = McpFiles.Current.Value ?? throw new InvalidOperationException("没有文件选择上下文");
                files.Request("file", "等用户选择中文文件");
                reachedAfterRequest = true;
                return Task.CompletedTask;
            }));
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.WorkCommand), null)).GetProperty("Id").GetString()!;
            Exception? interactionFailure = null;

            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    var waiting = Json(runtime.OperationState(id));
                    var request = waiting.GetProperty("fileRequest");
                    Assert.Equal("running", waiting.GetProperty("State").GetString());
                    Assert.Equal("file", request.GetProperty("Kind").GetString());
                    Assert.Equal("等用户选择中文文件", request.GetProperty("Title").GetString());
                }
                catch (Exception exception)
                {
                    interactionFailure = exception;
                }
                finally
                {
                    runtime.Cancel(id);
                }
            }));

            PumpUntil(() => State(runtime, id) is "cancelled" or "failed");
            if (interactionFailure is not null)
            {
                ExceptionDispatchInfo.Capture(interactionFailure).Throw();
            }

            Assert.Equal("cancelled", State(runtime, id));
            Assert.False(reachedAfterRequest);
            Assert.Equal(JsonValueKind.Null, Json(runtime.OperationState(id)).GetProperty("fileRequest").ValueKind);
            Assert.Null(McpFiles.Current.Value);
        });
    }

    [Fact]
    public void FileAnswerCancelReturnsControlToTheCommandWithoutCancellingTheOperation()
    {
        RunSta(dispatcher =>
        {
            var reachedAfterRequest = false;
            string[]? selected = [];
            var target = new TestCommands(new AsyncRelayCommand(() =>
            {
                var files = McpFiles.Current.Value ?? throw new InvalidOperationException("没有文件选择上下文");
                selected = files.Request("file", "选择文件");
                reachedAfterRequest = true;
                return Task.CompletedTask;
            }));
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var id = Json(runtime.Execute("projects", nameof(target.WorkCommand), null)).GetProperty("Id").GetString()!;
            Exception? interactionFailure = null;

            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    var requestId = Json(runtime.OperationState(id)).GetProperty("fileRequest").GetProperty("Id").GetString()!;
                    Assert.Throws<ArgumentException>(() => runtime.FileAnswer("wrong-request", null, false));
                    runtime.FileAnswer(requestId, null, false);
                }
                catch (Exception exception)
                {
                    interactionFailure = exception;
                    runtime.Cancel(id);
                }
            }));

            PumpUntil(() => State(runtime, id) is "completed" or "cancelled" or "failed");
            if (interactionFailure is not null)
            {
                ExceptionDispatchInfo.Capture(interactionFailure).Throw();
            }

            Assert.Equal("completed", State(runtime, id));
            Assert.True(reachedAfterRequest);
            Assert.Null(selected);
            Assert.False(Json(runtime.OperationState(id)).GetProperty("cancellationRequested").GetBoolean());
            Assert.Throws<InvalidOperationException>(() => runtime.FileAnswer("finished-request", null, false));
        });
    }

    [Fact]
    public void InspectReferencesCanBeUsedToSetTheSameViewModel()
    {
        RunSta(dispatcher =>
        {
            var target = new McpObjectsTests.TestViewModel();
            var runtime = new McpRuntime(new TestServices(target), dispatcher);
            var reference = Json(runtime.Inspect("projects", null, 0, 50)).GetProperty("reference").GetString()!;

            runtime.Set(reference, nameof(target.Name), Json("中文项目路径"));

            Assert.Equal("中文项目路径", target.Name);
            Assert.Throws<ArgumentException>(() => runtime.Inspect("unknown-reference", null, 0, 50));
            Assert.Throws<ArgumentException>(() => runtime.OperationState("unknown-operation"));
            Assert.Throws<ArgumentException>(() => runtime.Cancel("unknown-operation"));
            Assert.Throws<ArgumentException>(() => runtime.Execute(reference, "MissingCommand", null));
        });
    }

    private static string? State(McpRuntime runtime, string id) => Json(runtime.OperationState(id)).GetProperty("State").GetString();

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
        poll.Tick += (_, _) =>
        {
            try
            {
                if (done())
                {
                    frame.Continue = false;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                frame.Continue = false;
            }
        };
        timeout.Tick += (_, _) =>
        {
            failure = new TimeoutException("MCP 操作未在 5 秒内结束。");
            frame.Continue = false;
        };
        poll.Start();
        timeout.Start();

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            poll.Stop();
            timeout.Stop();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunSta(Action<Dispatcher> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                test(dispatcher);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "MCP 测试线程未在 10 秒内结束。");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TestServices(object target) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ProjectsViewModel) ? target : null;
    }

    private sealed class TestCommands
    {
        public TestCommands(IAsyncRelayCommand workCommand)
        {
            WorkCommand = workCommand;
            SyncCommand = new RelayCommand(() => SyncExecutions++);
        }

        public IAsyncRelayCommand WorkCommand { get; }

        public IRelayCommand SyncCommand { get; init; }

        public int SyncExecutions { get; private set; }
    }
}
