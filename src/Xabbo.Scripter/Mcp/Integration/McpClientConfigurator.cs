using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Mcp.Protocol;

namespace Xabbo.Scripter.Mcp.Integration;

public sealed record McpInstallResult(bool Ok, string Message, string? Path = null);

public sealed class McpClientConfigurator
{
    public const string ServerName = McpConstants.ServerName;
    private const string CodexServerName = "xabbo_scripter";

    private readonly McpConfig _config;

    public McpClientConfigurator(McpConfig config)
    {
        _config = config;
    }

    private string PlainUrl => _config.Endpoint;
    private string? Token => _config.RequireAuthToken ? _config.AuthToken : null;
    private string UrlWithToken => Token is null ? PlainUrl : $"{PlainUrl}?token={Token}";

    public object Snippets() => new
    {
        endpoint = PlainUrl,
        authToken = Token,
        claude = new { command = ClaudeCommand(), json = ClaudeJson() },
        gemini = new { command = GeminiCommand(), json = GeminiJson() },
        codex = new { toml = CodexToml() }
    };

    public string ClaudeCommand() =>
        $"claude mcp add --transport http {ServerName} \"{UrlWithToken}\"";

    public string ClaudeJson()
    {
        JsonObject server = new() { ["type"] = "http", ["url"] = UrlWithToken };
        return Wrap(server).ToJsonString(Indented);
    }

    public string GeminiCommand()
    {
        string header = Token is null ? "" : $" -H \"X-MCP-Token: {Token}\"";
        return $"gemini mcp add --transport http --scope user --trust{header} {ServerName} {PlainUrl}";
    }

    public string GeminiJson()
    {
        JsonObject server = new() { ["url"] = PlainUrl, ["type"] = "http", ["trust"] = true };
        if (Token is not null)
            server["headers"] = new JsonObject { ["X-MCP-Token"] = Token };
        return Wrap(server).ToJsonString(Indented);
    }

    public string CodexToml()
    {
        StringBuilder builder = new();
        builder.AppendLine($"[mcp_servers.{CodexServerName}]");
        builder.AppendLine($"url = \"{PlainUrl}\"");
        if (Token is not null)
            builder.AppendLine($"http_headers = {{ \"X-MCP-Token\" = \"{Token}\" }}");
        builder.AppendLine("startup_timeout_sec = 20");
        builder.AppendLine("tool_timeout_sec = 120");
        return builder.ToString();
    }

    public McpInstallResult InstallClaude()
    {
        string path = Path.Combine(UserProfile, ".claude.json");
        return MergeJsonServer(path, new JsonObject { ["type"] = "http", ["url"] = UrlWithToken });
    }

    public McpInstallResult InstallGemini()
    {
        string path = Path.Combine(UserProfile, ".gemini", "settings.json");

        JsonObject server = new() { ["url"] = PlainUrl, ["type"] = "http", ["trust"] = true };
        if (Token is not null)
            server["headers"] = new JsonObject { ["X-MCP-Token"] = Token };

        return MergeJsonServer(path, server);
    }

    public McpInstallResult InstallCodex()
    {
        string path = Path.Combine(UserProfile, ".codex", "config.toml");

        try
        {
            EnsureDirectory(path);

            string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

            if (existing.Contains($"[mcp_servers.{CodexServerName}]", StringComparison.Ordinal))
                return new McpInstallResult(true, "Already configured in Codex.", path);

            StringBuilder builder = new(existing);
            if (existing.Length > 0 && !existing.EndsWith("\n"))
                builder.AppendLine();
            builder.AppendLine();
            builder.Append(CodexToml());

            File.WriteAllText(path, builder.ToString());
            return new McpInstallResult(true, "Added to Codex config.", path);
        }
        catch (Exception ex)
        {
            return new McpInstallResult(false, ex.Message, path);
        }
    }

    private McpInstallResult MergeJsonServer(string path, JsonObject server)
    {
        try
        {
            EnsureDirectory(path);

            JsonObject root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            servers[ServerName] = server;

            File.WriteAllText(path, root.ToJsonString(Indented));
            return new McpInstallResult(true, "Configuration written.", path);
        }
        catch (Exception ex)
        {
            return new McpInstallResult(false, ex.Message, path);
        }
    }

    private static JsonObject Wrap(JsonObject server) =>
        new() { ["mcpServers"] = new JsonObject { [ServerName] = server } };

    private static void EnsureDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };
}
