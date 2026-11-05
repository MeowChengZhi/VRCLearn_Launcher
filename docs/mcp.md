# VRCLL MCP

此 Fork 在启动器进程内提供 MCP, 复用当前窗口的 ViewModel、RelayCommand、依赖解析、文件导入、模板和确认流程. 不维护第二套包安装器或直接修改启动器配置文件.

## 启动与连接

关闭已有的启动器后启动此版本:

```powershell
& "VRCLearnLauncher.exe" --mcp
```

默认端口为 39421, 可用 `--mcp-port 39422` 指定其他端口. 不带 `--mcp` 时不启动 MCP. 原有 `--environment <目录>` 仍可用, 适合隔离测试.

协议为官方 C# SDK 1.4.2 提供的 Streamable HTTP, 地址 `http://127.0.0.1:39421/mcp`. 只监听本机回环地址, 每次请求需要 `Authorization: Bearer <token>`. 不接受浏览器跨站 Origin 请求.

首次启动在数据目录的 `vrclearn/mcp-token` 生成本机令牌. `vrclearn/mcp.json` 记录实际地址、令牌文件路径、进程 ID 与数据目录, 不包含令牌正文. 退出后移除发现文件. 令牌文件不要上传到 GitHub 或分享给其他人.

客户端配置示例:

```json
{
  "mcpServers": {
    "vrcll": {
      "type": "http",
      "url": "http://127.0.0.1:39421/mcp",
      "headers": { "Authorization": "Bearer <本机令牌>" }
    }
  }
}
```

客户端的配置字段可能不同, 使用其 Streamable HTTP 和自定义请求头设置即可.

## 工具

| 工具 | 用途 |
|---|---|
| `launcher_state` | 当前页面、全部模型入口、窗口与活动操作 |
| `launcher_inspect` | 读取字段、可写状态、原生命令、参数类型、集合分页 |
| `launcher_set` | 修改模型的公开可写属性, 执行已有变更逻辑 |
| `launcher_execute` | 调用原生命令, 返回异步操作 ID |
| `launcher_navigate` | 打开页面并触发该页面原有加载 |
| `launcher_open_project` | 打开 Unity 项目详情 |
| `launcher_import` | 调用项目、备份、模板、插件的原生拖放导入 |
| `launcher_operation` | 操作状态、错误、取消能力与待答复文件选择 |
| `launcher_cancel` | 请求取消当前操作 |
| `launcher_file_answer` | 文件、目录、多文件和保存位置选择, 或取消 |
| `launcher_windows` | 主窗口、确认框、编辑器和插件窗口 |
| `launcher_ui` | 读取可见原生控件的文字、值、选项和启用状态 |
| `launcher_ui_set` | 编辑文本、勾选、下拉、数值和滚动控件 |
| `launcher_ui_click` | 执行原生按钮/菜单事件与命令 |
| `launcher_close_window` | 通过原生关闭流程取消子窗口 |

模型入口覆盖 `main`、`projects`、`project-detail`、`repositories`、`templates`、`resources`、`backups`、`plugins`、`settings`、`shared-settings`、`package-cache`、`user-packages`、`updates`、`logs` 和 `setup`. 模态窗口的数据模型通过 `launcher_windows` 返回的 `model.reference` 访问.

项目新建、导入、收藏、启动、重命名、备份与归档; 仓库添加、排序、导入与隐藏; 包安装、升级、切换版本、重装、删除与应用变更; 模板编辑与依赖; 资源和标签; 插件安装、启用与卸载; 缓存、网络和启动参数等功能使用同一套发现和调用方式. 界面独有的按钮事件通过原生控件工具操作.

只公开已经存在的属性和命令, 不提供任意 C# 执行、私有字段访问、反射方法调用或通用文件读写工具.

## 操作顺序

1. `launcher_state` 确定实例、当前页和是否有未完成操作.
2. `launcher_navigate` 打开目标页. 页面原有加载可能异步进行, 检查 `IsBusy`、`Status` 及相关列表.
3. `launcher_inspect` 读取可用属性/命令; 对集合指定 `property` 并分页, 使用返回的对象 `reference` 定位条目.
4. 设置输入字段后执行命令. 对象参数传 `{"reference":"object-..."}`, 不复制对象内容或猜测列表下标.
5. 用 `launcher_operation` 跟进. 文件选择通过 `fileRequest` 与 `launcher_file_answer` 完成. 保存到已有文件需要 `overwrite=true`.
6. 确认框保留真实窗口. 用 `launcher_windows`、`launcher_ui` 读取提示与按钮, 取得用户所需授权后点击. 不自动同意删除、覆盖、插件信任或其他确认.
7. 操作返回后重新检查页面业务结果. `completed` 只表示原生命令返回, 不保证安装/构建成功. 对原生代码已捕获的失败, 看页面 `Status`、提示、包列表或待应用变更.

修改操作一次运行一个, 其他客户端可继续读取并回答当前子窗口. 操作取消采用原生命令的取消能力, 不强杀进程. 文件选择可以取消. 原生不可取消任务或仍打开的模态窗口需要按原有流程结束.

对象引用是当前进程内的弱引用. 列表重排不会让已有引用指向其他对象, 失效后需要重新读取. 集合每页最多 200 项. 所有模型/控件访问都调度到 WPF UI 线程, MCP 监听在后台运行.

## 构建

使用仓库现有 GitHub Actions Build 工作流. 自包含便携版位于 `VRCLearnLauncher-MCP-portable` artifact, 安装包仍由原有步骤生成. MCP 临时测试存在时在 Windows runner 执行, 报告位于 `MCP-test-report` artifact.
