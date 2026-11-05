using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using VRCLearn.App.Mcp;
using Xunit;

namespace VRCLearn.Mcp.Tests;

public sealed class McpObjectsTests
{
    [Fact]
    public void ReferencesKeepObjectIdentity()
    {
        var objects = new McpObjects();
        var first = new TestItem("橙汁 avatar");
        var equal = new TestItem("橙汁 avatar");

        var reference = objects.Reference(first);

        Assert.Equal(reference, objects.Reference(first));
        Assert.NotEqual(reference, objects.Reference(equal));
        Assert.Same(first, objects.Resolve(reference));
        Assert.Throws<ArgumentException>(() => objects.Resolve("object-missing"));
    }

    [Fact]
    public void DescribeOnlyExposesPublicInstanceProperties()
    {
        var objects = new McpObjects();
        var target = new TestViewModel();

        var description = Json(objects.Describe(target));
        var properties = description.GetProperty("properties");

        Assert.Equal("TestViewModel", description.GetProperty("type").GetString());
        Assert.Equal(target.Name, properties.GetProperty(nameof(target.Name)).GetProperty("value").GetString());
        Assert.True(properties.GetProperty(nameof(target.Name)).GetProperty("writable").GetBoolean());
        Assert.False(properties.GetProperty(nameof(target.PrivateSetter)).GetProperty("writable").GetBoolean());
        Assert.False(properties.TryGetProperty("Secret", out _));
        Assert.False(properties.TryGetProperty("_name", out _));
        Assert.False(properties.TryGetProperty(nameof(TestViewModel.StaticValue), out _));
        Assert.False(properties.TryGetProperty(nameof(target.PrivateGetter), out _));
        Assert.False(properties.TryGetProperty("Item", out _));
        Assert.False(properties.TryGetProperty(nameof(target.SyncCommand), out _));
        Assert.Equal("读取失败", properties.GetProperty(nameof(target.Throwing)).GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty(nameof(target.RuntimeType)).GetProperty("value").ValueKind);

        var commands = description.GetProperty("commands").EnumerateArray().ToList();
        var sync = Assert.Single(commands, command => command.GetProperty("name").GetString() == nameof(target.SyncCommand));
        Assert.True(sync.GetProperty("canExecuteWithoutParameter").GetBoolean());
        Assert.False(sync.GetProperty("isAsync").GetBoolean());
        var parameterized = Assert.Single(commands, command => command.GetProperty("name").GetString() == nameof(target.SelectCommand));
        Assert.Equal(nameof(TestItem), parameterized.GetProperty("parameterType").GetString());
        Assert.Equal(JsonValueKind.Null, parameterized.GetProperty("canExecuteWithoutParameter").ValueKind);
    }

    [Fact]
    public void InspectPagesItemsAndPreservesTheirReferences()
    {
        var objects = new McpObjects();
        var first = new TestItem("项目一");
        var second = new TestItem("项目二");
        var target = new TestViewModel { Items = [first, second, first] };

        var page = Json(objects.Inspect(target, nameof(target.Items), 0, 2));
        var next = Json(objects.Inspect(target, nameof(target.Items), 2, 2));
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Collection(items,
            item => Assert.Equal("项目一", item.GetProperty("properties").GetProperty(nameof(TestItem.Name)).GetProperty("value").GetString()),
            item => Assert.Equal("项目二", item.GetProperty("properties").GetProperty(nameof(TestItem.Name)).GetProperty("value").GetString()));
        Assert.Equal(items[0].GetProperty("reference").GetString(), Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        Assert.False(next.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, next.GetProperty("offset").GetInt32());
        Assert.Equal(page.GetProperty("reference").GetString(), next.GetProperty("reference").GetString());
    }

    [Fact]
    public void InspectHandlesEmptyAndOutOfRangePages()
    {
        var objects = new McpObjects();
        var empty = Json(objects.Inspect(Array.Empty<string>()));
        var beyond = Json(objects.Inspect(new[] { "喵", "w" }, offset: 5));

        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.False(empty.GetProperty("hasMore").GetBoolean());
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.False(beyond.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public void InspectEnumeratesOnlyTheRequestedPageAndLookahead()
    {
        var objects = new McpObjects();
        var reads = 0;

        IEnumerable<int> Sequence()
        {
            for (var value = 0; value < 20; value++)
            {
                reads++;
                yield return value;
            }
        }

        var page = Json(objects.Inspect(Sequence(), offset: 3, limit: 2));

        Assert.Equal(6, reads);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("total").ValueKind);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(new[] { 3, 4 }, page.GetProperty("items").EnumerateArray().Select(item => item.GetInt32()));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 201)]
    public void InspectRejectsInvalidPageBounds(int offset, int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new McpObjects().Inspect(Array.Empty<string>(), offset: offset, limit: limit));
    }

    [Fact]
    public void InspectAcceptsMaximumPageSizeAndScalarValues()
    {
        var objects = new McpObjects();
        var values = new object?[] { "中文", 7, null, TestMode.Ready };

        var page = Json(objects.Inspect(values, limit: 200));
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal("中文", items[0].GetString());
        Assert.Equal(7, items[1].GetInt32());
        Assert.Equal(JsonValueKind.Null, items[2].ValueKind);
        Assert.Equal(nameof(TestMode.Ready), items[3].GetString());
        Assert.Throws<ArgumentException>(() => objects.Inspect(new TestViewModel(), nameof(TestViewModel.Selected)));
    }

    [Fact]
    public void SetUsesThePublicSetterAndPreservesChinesePaths()
    {
        var objects = new McpObjects();
        var target = new TestViewModel();
        const string path = @"D:\橙汁的VRC改模摸喵\我的 avatar\模型.unitypackage";

        objects.Set(target, nameof(target.Name), Json(path));
        objects.Set(target, nameof(target.Count), Parse("42"));
        objects.Set(target, nameof(target.Enabled), Parse("true"));
        objects.Set(target, nameof(target.Mode), Parse("\"Ready\""));

        Assert.Equal(path, target.Name);
        Assert.Equal(1, target.NameWrites);
        Assert.Equal(42, target.Count);
        Assert.True(target.Enabled);
        Assert.Equal(TestMode.Ready, target.Mode);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Secret")]
    [InlineData("_name")]
    [InlineData("PrivateSetter")]
    [InlineData("PrivateGetter")]
    [InlineData("StaticValue")]
    [InlineData("Item")]
    [InlineData("SyncCommand")]
    public void SetRejectsUnavailableOrNonWritableMembers(string name)
    {
        Assert.Throws<ArgumentException>(() => new McpObjects().Set(new TestViewModel(), name, Json("changed")));
    }

    [Fact]
    public void ConvertHandlesSupportedScalarTypesAndNulls()
    {
        var objects = new McpObjects();
        var id = Guid.Parse("4778e079-7342-4ac7-a09d-515999fbe961");

        Assert.Equal(12, Assert.IsType<int>(objects.Convert(Parse("12"), typeof(int))));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(objects.Convert(Parse("9223372036854775807"), typeof(long))));
        Assert.Equal(1.25m, Assert.IsType<decimal>(objects.Convert(Parse("1.25"), typeof(decimal))));
        Assert.Equal(1.25d, Assert.IsType<double>(objects.Convert(Parse("1.25"), typeof(double))));
        Assert.True(Assert.IsType<bool>(objects.Convert(Parse("true"), typeof(bool))));
        Assert.Equal(TestMode.Ready, Assert.IsType<TestMode>(objects.Convert(Parse("\"Ready\""), typeof(TestMode))));
        Assert.Equal(id, Assert.IsType<Guid>(objects.Convert(Json(id.ToString()), typeof(Guid))));
        Assert.Equal(TimeSpan.FromMinutes(2), Assert.IsType<TimeSpan>(objects.Convert(Parse("\"00:02:00\""), typeof(TimeSpan))));
        Assert.Equal(new[] { "中文路径", "avatar space" }, Assert.IsType<string[]>(objects.Convert(Parse("[\"中文路径\",\"avatar space\"]"), typeof(string[]))));
        Assert.Null(objects.Convert(Parse("null"), typeof(int?)));
        Assert.Null(objects.Convert(Parse("null"), typeof(string)));
        Assert.Null(objects.Convert(Parse("null"), typeof(string[])));
        Assert.Null(objects.Convert(Parse("null"), typeof(TestItem)));
        Assert.Equal(7, Assert.IsType<int>(objects.Convert(Parse("7"), typeof(int?))));
    }

    [Theory]
    [InlineData("\"invalid\"")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("true")]
    public void ConvertRejectsInvalidIntegerValues(string json)
    {
        Assert.Throws<JsonException>(() => new McpObjects().Convert(Parse(json), typeof(int)));
    }

    [Fact]
    public void ConvertRejectsInvalidEnumsAndNonNullableNull()
    {
        var objects = new McpObjects();

        Assert.Throws<JsonException>(() => objects.Convert(Parse("\"MissingMode\""), typeof(TestMode)));
        Assert.Throws<ArgumentException>(() => objects.Convert(Parse("null"), typeof(int)));
        Assert.Throws<ArgumentException>(() => objects.Convert(Parse("{\"Name\":\"untrusted\"}"), typeof(TestItem)));
    }

    [Fact]
    public void ObjectReferencesWorkForPropertiesAndCommandParameters()
    {
        var objects = new McpObjects();
        var target = new TestViewModel();
        var item = new TestItem("同一个项目");
        var argument = Json(new { reference = objects.Reference(item) });

        objects.Set(target, nameof(target.Selected), argument);
        Assert.Same(item, target.Selected);
        objects.Set(target, nameof(target.Selected), Parse("null"));
        Assert.Null(target.Selected);

        var call = objects.Command(target, nameof(target.SelectCommand), argument);
        Assert.Same(item, call.Parameter);
        Assert.Null(target.Selected);
        call.Command.Execute(call.Parameter);
        Assert.Same(item, target.Selected);
        Assert.Throws<ArgumentException>(() => objects.Convert(argument, typeof(string)));
        Assert.Throws<ArgumentException>(() => objects.Convert(Json(new { reference = "missing" }), typeof(TestItem)));
    }

    [Fact]
    public void CommandsAreResolvedWithoutBeingExecuted()
    {
        var objects = new McpObjects();
        var target = new TestViewModel();

        var call = objects.Command(target, nameof(target.SyncCommand), null);

        Assert.Equal(0, target.Executions);
        Assert.Null(call.Parameter);
        call.Command.Execute(call.Parameter);
        Assert.Equal(1, target.Executions);

        var count = objects.Command(target, nameof(target.CountCommand), Parse("9"));
        count.Command.Execute(count.Parameter);
        Assert.Equal(9, target.Count);
    }

    [Fact]
    public void CommandsRejectDisabledAndInvalidRequestsWithoutExecuting()
    {
        var objects = new McpObjects();
        var target = new TestViewModel();

        Assert.Throws<InvalidOperationException>(() => objects.Command(target, nameof(target.DisabledCommand), Json("value")));
        Assert.Throws<ArgumentException>(() => objects.Command(target, nameof(target.SyncCommand), Json("unexpected")));
        Assert.Throws<ArgumentException>(() => objects.Command(target, nameof(target.Name), null));
        Assert.Throws<ArgumentException>(() => objects.Command(target, "MissingCommand", null));
        Assert.Throws<ArgumentException>(() => objects.Command(target, "SecretCommand", null));
        Assert.Throws<ArgumentException>(() => objects.Command(target, nameof(target.OrdinaryMethod), null));
        Assert.Throws<JsonException>(() => objects.Command(target, nameof(target.CountCommand), Json("invalid")));
        Assert.Equal(0, target.Executions);
    }

    [Fact]
    public async Task AsyncCommandsKeepTheirCompletionAndFailure()
    {
        var objects = new McpObjects();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new AsyncViewModel(new AsyncRelayCommand(() => completion.Task));
        var call = objects.Command(target, nameof(target.WorkCommand), null);
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(call.Command);
        var description = Json(objects.Describe(target));

        Assert.True(Assert.Single(description.GetProperty("commands").EnumerateArray()).GetProperty("isAsync").GetBoolean());
        var execution = command.ExecuteAsync(call.Parameter);
        Assert.True(command.IsRunning);
        completion.SetResult();
        await execution;
        Assert.False(command.IsRunning);

        var failed = new AsyncViewModel(new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("操作失败"))));
        var failedCall = objects.Command(failed, nameof(failed.WorkCommand), null);
        var failedCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(failedCall.Command);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failedCommand.ExecuteAsync(failedCall.Parameter));
        Assert.Equal("操作失败", exception.Message);
        Assert.False(failedCommand.IsRunning);
    }

    [Fact]
    public async Task AsyncCommandsKeepCancellation()
    {
        var objects = new McpObjects();
        var target = new AsyncViewModel(new AsyncRelayCommand(token => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        var call = objects.Command(target, nameof(target.WorkCommand), null);
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(call.Command);

        var execution = command.ExecuteAsync(call.Parameter);
        Assert.True(command.CanBeCanceled);
        command.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(command.IsCancellationRequested);
        Assert.False(command.IsRunning);
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public enum TestMode
    {
        None,
        Ready,
    }

    public sealed record TestItem(string Name);

    public sealed class TestViewModel
    {
        private string _name = "初始名称";

        public TestViewModel()
        {
            SyncCommand = new RelayCommand(() => Executions++);
            CountCommand = new RelayCommand<int>(value => Count = value);
            SelectCommand = new RelayCommand<TestItem>(item => Selected = item);
            DisabledCommand = new RelayCommand<string>(_ => Executions++, _ => false);
        }

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                NameWrites++;
            }
        }

        public int NameWrites { get; private set; }

        public int Count { get; set; }

        public bool Enabled { get; set; }

        public TestMode Mode { get; set; }

        public TestItem? Selected { get; set; }

        public List<TestItem> Items { get; set; } = [];

        public int Executions { get; private set; }

        public string PrivateSetter { get; private set; } = "只读";

        public string PrivateGetter { private get; set; } = "不可读";

        public static string StaticValue => "静态";

        public string this[int index] => index.ToString();

        public string Throwing => throw new InvalidOperationException("读取失败");

        public Type RuntimeType => typeof(TestViewModel);

        public IRelayCommand SyncCommand { get; }

        public IRelayCommand<int> CountCommand { get; }

        public IRelayCommand<TestItem> SelectCommand { get; }

        public IRelayCommand<string> DisabledCommand { get; }

        private string Secret => "私有";

        private IRelayCommand SecretCommand => SyncCommand;

        public void OrdinaryMethod() => Executions++;
    }

    private sealed class AsyncViewModel(IAsyncRelayCommand workCommand)
    {
        public IAsyncRelayCommand WorkCommand { get; } = workCommand;
    }
}
