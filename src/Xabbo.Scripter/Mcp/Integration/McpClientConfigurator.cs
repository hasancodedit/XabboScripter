using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Mcp.Protocol;

namespace Xabbo.Scripter.Mcp.Integration;

public sealed record McpClientTarget(string Id, string Name, string Hint, string CopyText);

public sealed class McpClientConfigurator
{
    public const string ServerName = McpConstants.ServerName;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly McpConfig _config;

    public McpClientConfigurator(McpConfig config)
    {
        _config = config;
    }

    private string Url => _config.Endpoint;
    private string? Token => _config.RequireAuthToken && !string.IsNullOrEmpty(_config.AuthToken) ? _config.AuthToken : null;
    private string Bearer => $"Bearer {Token}";

    public IReadOnlyList<McpClientTarget> Targets() => new List<McpClientTarget>
    {
        new("claude-code", "Claude Code", "CLI",
            $"claude mcp add --transport http {ServerName} {Url} --scope user" + (Token is null ? "" : $" --header \"Authorization: {Bearer}\"")),

        new("gemini", "Gemini CLI", "CLI",
            $"gemini mcp add --scope user --transport http {(Token is null ? "" : $"--header \"Authorization: {Bearer}\" ")}{ServerName} {Url}"),

        new("codex", "Codex", "CLI and IDE", CodexToml()),

        new("cursor", "Cursor", "IDE", Snippet("mcpServers", Server("url"))),

        new("windsurf", "Windsurf", "IDE", Snippet("mcpServers", Server("serverUrl"))),

        new("antigravity", "Antigravity", "CLI and IDE", Snippet("mcpServers", Server("serverUrl"))),

        new("vscode", "VS Code", "IDE",
            $"code --add-mcp \"{Escape(VsCodeServer().ToJsonString())}\""),

        new("opencode", "opencode", "CLI", Snippet("mcp", Server("url", "remote", ("enabled", true)))),
    };

    private JsonObject Server(string urlField, string? type = null, params (string Key, JsonNode? Value)[] extra)
    {
        JsonObject server = new();
        if (type is not null) server["type"] = type;
        server[urlField] = Url;
        if (Token is not null) server["headers"] = new JsonObject { ["Authorization"] = Bearer };
        foreach ((string key, JsonNode? value) in extra)
            server[key] = value;
        return server;
    }

    private JsonObject VsCodeServer()
    {
        JsonObject server = new() { ["name"] = ServerName, ["type"] = "http", ["url"] = Url };
        if (Token is not null) server["headers"] = new JsonObject { ["Authorization"] = Bearer };
        return server;
    }

    private string CodexToml()
    {
        StringBuilder builder = new();
        builder.AppendLine($"[mcp_servers.{ServerName}]");
        builder.AppendLine($"url = \"{Url}\"");
        if (Token is not null)
            builder.AppendLine($"http_headers = {{ Authorization = \"{Bearer}\" }}");
        return builder.ToString().TrimEnd();
    }

    private string Snippet(string rootKey, JsonObject server) =>
        new JsonObject { [rootKey] = new JsonObject { [ServerName] = server } }.ToJsonString(Indented);

    private static string Escape(string json) => json.Replace("\"", "\\\"");
}
