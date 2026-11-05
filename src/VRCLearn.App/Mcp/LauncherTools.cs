using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace VRCLearn.App.Mcp;

[McpServerToolType]
public sealed class LauncherTools(McpRuntime runtime)
{
    [McpServerTool(Name = "launcher_state", ReadOnly = true, OpenWorld = false)]
    [Description("读取 VRCLL 当前页面、可用目标、活动操作和窗口。所有功能由原生界面模型与命令执行。先用 inspect 发现字段和命令, 不猜测名称。")]
    public Task<object> State(CancellationToken cancellationToken) => runtime.OnUi(runtime.State, cancellationToken);

    [McpServerTool(Name = "launcher_inspect", ReadOnly = true, OpenWorld = false)]
    [Description("读取页面别名或 reference 对象的公开属性、可写状态和原生命令。property 可展开集合, offset/limit 分页。返回的 reference 可作为参数 {\"reference\":\"...\"}, 不按可变列表下标猜对象。")]
    public Task<object> Inspect(string target, string? property = null, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) =>
        runtime.OnUi(() => runtime.Inspect(target, property, offset, limit), cancellationToken);

    [McpServerTool(Name = "launcher_set", OpenWorld = false)]
    [Description("设置 inspect 标记 writable 的原生模型字段。值按字段类型校验, 下拉选项/对象用 reference, null 清空可空字段。设置会执行该字段原有逻辑。")]
    public Task<object> Set(string target, string property, JsonElement value, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.Set(target, property, value), cancellationToken);

    [McpServerTool(Name = "launcher_execute", OpenWorld = false)]
    [Description("执行 inspect 返回的原生 RelayCommand, 复用 VRCLL 的安装、依赖解析、备份、模板、资源、插件、设置和确认流程。先检查 command 参数类型。立即返回操作ID, 通过 operation 和 windows 跟进。")]
    public Task<object> Execute(string target, string command, JsonElement? argument = null,
        CancellationToken cancellationToken = default) =>
        runtime.OnUi(() => runtime.Execute(target, command, argument), cancellationToken);

    [McpServerTool(Name = "launcher_navigate", Destructive = false, OpenWorld = false)]
    [Description("打开 VRCLL 页面并执行页面本来的加载逻辑。支持 projects/project-detail/repositories/templates/resources/backups/plugins/settings/logs/setup。加载可能异步进行, 用 inspect 检查 IsBusy/Status。")]
    public Task<object> Navigate(string page, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.Navigate(page), cancellationToken);

    [McpServerTool(Name = "launcher_open_project", Destructive = false, OpenWorld = false)]
    [Description("通过原生项目详情流程打开指定 Unity 项目。返回操作ID; 随后 inspect project-detail 查看已安装/可安装包、待应用变更、Unity版本和启动参数。")]
    public Task<object> OpenProject(string path, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.OpenProject(path), cancellationToken);

    [McpServerTool(Name = "launcher_import", OpenWorld = false)]
    [Description("调用 VRCLL 原生拖放导入流程。page 为 projects/backups/templates/plugins, paths 为目录或本地文件完整路径。插件仍经过原生检查与信任确认。")]
    public Task<object> Import(string page, string[] paths, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.Import(page, paths), cancellationToken);

    [McpServerTool(Name = "launcher_operation", ReadOnly = true, OpenWorld = false)]
    [Description("查询 queued/running/completed/failed/cancelled 状态与待答复文件请求。completed 仅表示原生命令已返回, 业务结果还应核对页面 Status、安装列表和待应用变更。")]
    public Task<object> Operation(string id, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.OperationState(id), cancellationToken);

    [McpServerTool(Name = "launcher_cancel", Destructive = false, OpenWorld = false)]
    [Description("请求取消 MCP 操作, 并调用原生命令的取消接口。取消待选文件按取消处理。不能取消的原生工作继续结束, 本工具不强杀进程。")]
    public Task<object> Cancel(string id, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.Cancel(id), cancellationToken);

    [McpServerTool(Name = "launcher_file_answer", OpenWorld = false)]
    [Description("答复 operation.fileRequest 的文件/目录/多选/保存选择。paths=null 表示取消; 保存到已有文件须明确 overwrite=true。路径仍由 VRCLL 原生导入/导出逻辑处理。")]
    public Task<object> FileAnswer(string requestId, string[]? paths, bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        runtime.OnUi(() => runtime.FileAnswer(requestId, paths, overwrite), cancellationToken);

    [McpServerTool(Name = "launcher_windows", ReadOnly = true, OpenWorld = false)]
    [Description("列出 VRCLL 可见窗口及其数据模型引用, 包括确认框、模板编辑器、新建项目、资源编辑与插件窗口。")]
    public Task<object> Windows(CancellationToken cancellationToken) => runtime.OnUi(runtime.Windows, cancellationToken);

    [McpServerTool(Name = "launcher_ui", ReadOnly = true, OpenWorld = false)]
    [Description("读取指定窗口的可见原生控件, 包含文字、按钮、输入框、选项、启用状态及模型引用。先读窗口内容再操作确认按钮。offset/limit 分页。")]
    public Task<object> Ui(string window, int offset = 0, int limit = 100,
        CancellationToken cancellationToken = default) =>
        runtime.OnUi(() => runtime.Ui(window, offset, limit), cancellationToken);

    [McpServerTool(Name = "launcher_ui_set", OpenWorld = false)]
    [Description("操作 ui 返回的原生控件。文本框填字符串, 勾选填布尔, 数值/滚动填数字, 下拉填选项 reference 或0起始序号。保留数据绑定并更新源。密码框不支持。")]
    public Task<object> UiSet(string control, JsonElement value, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.UiSet(control, value), cancellationToken);

    [McpServerTool(Name = "launcher_ui_click", OpenWorld = false)]
    [Description("点击 ui 返回的启用按钮/菜单项, 执行原有事件与命令。用于确认/取消/额外按钮和界面特有操作。删除、覆盖、信任等按钮仍由调用者先获得用户授权。")]
    public Task<object> UiClick(string control, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.UiClick(control), cancellationToken);

    [McpServerTool(Name = "launcher_close_window", Destructive = false, OpenWorld = false)]
    [Description("按原生关闭流程关闭子窗口, 例如取消对话框。不能通过此工具退出主程序。")]
    public Task<object> CloseWindow(string window, CancellationToken cancellationToken) =>
        runtime.OnUi(() => runtime.CloseWindow(window), cancellationToken);
}
