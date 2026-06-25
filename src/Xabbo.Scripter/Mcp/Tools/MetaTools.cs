using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Mcp.Integration;
using Xabbo.Scripter.Mcp.Protocol;
using Xabbo.Scripter.Mcp.Server;
using Xabbo.Scripter.Services;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class MetaTools : IMcpToolProvider
{
    private readonly IServiceProvider _services;
    private readonly McpConfig _config;
    private readonly IScriptHost _host;
    private readonly McpClientConfigurator _configurator;

    public MetaTools(IServiceProvider services, McpConfig config, IScriptHost host, McpClientConfigurator configurator)
    {
        _services = services;
        _config = config;
        _host = host;
        _configurator = configurator;
    }

    [McpTool("get_server_info", "Get this MCP server's status: whether it is running, its endpoint, request counts, the number of available tools, the scripter's game connection state and the enabled capabilities.")]
    public object GetServerInfo()
    {
        McpServer server = _services.GetRequiredService<McpServer>();
        McpToolRegistry registry = _services.GetRequiredService<McpToolRegistry>();

        return new
        {
            name = McpConstants.ServerName,
            running = server.IsRunning,
            endpoint = server.Endpoint,
            requestCount = server.RequestCount,
            sessionCount = server.SessionCount,
            toolCount = registry.Tools.Count,
            scripterConnected = _host.CanExecute,
            permissions = new
            {
                execute = _config.AllowExecute,
                fileWrite = _config.AllowFileWrite,
                editor = _config.AllowEditor
            },
            authRequired = _config.RequireAuthToken
        };
    }

    [McpTool("list_mcp_tools", "List every tool this MCP server exposes, with its description and input schema, so you can discover your own capabilities.")]
    public object ListMcpTools()
    {
        McpToolRegistry registry = _services.GetRequiredService<McpToolRegistry>();

        return new
        {
            count = registry.Tools.Count,
            tools = registry.Tools.Select(t => new { t.Name, t.Description, inputSchema = t.InputSchema }).ToList()
        };
    }

    [McpTool("get_integration", "Get ready-to-use connection details for external AI clients (Claude Code, Gemini, Codex, Cursor, VS Code, and many more): the endpoint, auth token and a per-client copy/install snippet or command.")]
    public object GetIntegration() => new
    {
        endpoint = _config.Endpoint,
        authToken = _config.RequireAuthToken ? _config.AuthToken : null,
        clients = _configurator.Targets()
    };
}
