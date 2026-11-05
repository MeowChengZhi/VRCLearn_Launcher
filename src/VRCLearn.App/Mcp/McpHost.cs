using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCLearn.Core.Configuration;

namespace VRCLearn.App.Mcp;

public static class McpAccess
{
    public static bool Allows(string? authorization, string token, string host, int? port, int expectedPort, string? origin)
    {
        if (host is not ("127.0.0.1" or "localhost") || port != expectedPort || !string.IsNullOrEmpty(origin))
        {
            return false;
        }
        if (string.IsNullOrEmpty(token) || authorization is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes($"Bearer {token}"));
    }
}

public sealed class McpHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly string _discoveryPath;

    private McpHost(WebApplication application, string discoveryPath)
    {
        _application = application;
        _discoveryPath = discoveryPath;
    }

    public static async Task<McpHost> StartAsync(IServiceProvider services, AppPaths paths, string[] args)
    {
        var portIndex = Array.IndexOf(args, "--mcp-port");
        var port = portIndex >= 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1]) : 39421;
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "MCP 端口必须为 1024..65535。");
        }

        var tokenPath = Path.Combine(paths.PrivateDirectory, "mcp-token");
        var token = File.Exists(tokenPath) ? (await File.ReadAllTextAsync(tokenPath)).Trim() : "";
        if (token.Length == 0)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(tokenPath, token, new UTF8Encoding(false));
        }

        var endpoint = $"http://127.0.0.1:{port}/mcp";
        var runtime = new McpRuntime(services, System.Windows.Application.Current.Dispatcher);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHost).Assembly.GetName().Name,
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(runtime);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<LauncherTools>();

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (!McpAccess.Allows(context.Request.Headers.Authorization.ToString(), token,
                context.Request.Host.Host, context.Request.Host.Port, port, context.Request.Headers.Origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        application.MapMcp("/mcp");

        try
        {
            await application.StartAsync();
            var discoveryPath = Path.Combine(paths.PrivateDirectory, "mcp.json");
            await File.WriteAllTextAsync(discoveryPath, JsonSerializer.Serialize(new
            {
                endpoint, tokenFile = tokenPath, processId = Environment.ProcessId,
                environment = paths.Root, transport = "streamable-http",
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            services.GetRequiredService<ILogger<McpHost>>().LogInformation("MCP 已启动: {Endpoint}", endpoint);
            return new McpHost(application, discoveryPath);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        File.Delete(_discoveryPath);
    }
}
