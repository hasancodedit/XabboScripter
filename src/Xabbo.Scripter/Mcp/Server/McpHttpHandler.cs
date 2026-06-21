using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Mcp.Protocol;

namespace Xabbo.Scripter.Mcp.Server;

public interface IMcpActivitySink
{
    void OnRequest(string? method);
    void OnSessionOpened(string sessionId);
}

public sealed class McpHttpHandler
{
    private const string EndpointPath = "/mcp";

    private readonly McpDispatcher _dispatcher;
    private readonly McpConfig _config;
    private readonly IMcpActivitySink _sink;

    public McpHttpHandler(McpDispatcher dispatcher, McpConfig config, IMcpActivitySink sink)
    {
        _dispatcher = dispatcher;
        _config = config;
        _sink = sink;
    }

    public async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        HttpResponse response = context.Response;

        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-MCP-Token, Mcp-Session-Id, MCP-Protocol-Version";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";

        bool originAllowed = IsOriginAllowed(request, out string? origin);
        if (originAllowed && origin is not null)
            response.Headers["Access-Control-Allow-Origin"] = origin;

        if (HttpMethods.IsOptions(request.Method))
        {
            response.StatusCode = originAllowed ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden;
            return;
        }

        if (!IsEndpoint(request.Path))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!originAllowed)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!IsAuthorized(request))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            await response.WriteAsync("Missing or invalid authentication token.").ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await HandlePostAsync(context).ConfigureAwait(false);
    }

    private async Task HandlePostAsync(HttpContext context)
    {
        string body;
        using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context, JsonRpcResponse.Failure(McpJson.Null, JsonRpcErrorCodes.ParseError, "Parse error.")).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    await WriteJsonAsync(context, JsonRpcResponse.Failure(McpJson.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: empty batch.")).ConfigureAwait(false);
                    return;
                }

                List<JsonRpcResponse> responses = new();
                foreach (JsonElement element in root.EnumerateArray())
                {
                    JsonRpcResponse? response = await HandleElementAsync(context, element).ConfigureAwait(false);
                    if (response is not null)
                        responses.Add(response);
                }

                if (responses.Count == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await WriteJsonAsync(context, responses).ConfigureAwait(false);
            }
            else
            {
                JsonRpcResponse? response = await HandleElementAsync(context, root).ConfigureAwait(false);
                if (response is null)
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await WriteJsonAsync(context, response).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonRpcResponse?> HandleElementAsync(HttpContext context, JsonElement element)
    {
        JsonRpcRequest request;
        try
        {
            request = element.Deserialize<JsonRpcRequest>(McpJson.Wire)!;
        }
        catch (JsonException)
        {
            return JsonRpcResponse.Failure(McpJson.Null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request.");
        }

        _sink.OnRequest(request.Method);

        if (string.Equals(request.Method, McpMethods.Initialize, StringComparison.Ordinal))
        {
            string sessionId = Guid.NewGuid().ToString("N");
            context.Response.Headers[McpConstants.SessionHeader] = sessionId;
            _sink.OnSessionOpened(sessionId);
        }

        return await _dispatcher.DispatchAsync(request, context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (!_config.RequireAuthToken)
            return true;

        string expected = _config.AuthToken;
        if (string.IsNullOrEmpty(expected))
            return false;

        if (request.Headers.TryGetValue("X-MCP-Token", out var headerToken) &&
            TokenEquals(headerToken.ToString(), expected))
            return true;

        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            string value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                TokenEquals(value["Bearer ".Length..].Trim(), expected))
                return true;
        }

        if (request.Query.TryGetValue("token", out var queryToken) &&
            TokenEquals(queryToken.ToString(), expected))
            return true;

        return false;
    }

    private static bool TokenEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    private static bool IsEndpoint(PathString path)
    {
        string value = path.Value ?? string.Empty;
        return value.Equals(EndpointPath, StringComparison.Ordinal) ||
               value.Equals(EndpointPath + "/", StringComparison.Ordinal);
    }

    private static bool IsOriginAllowed(HttpRequest request, out string? origin)
    {
        origin = null;

        if (!request.Headers.TryGetValue("Origin", out var originHeader))
            return true;

        string value = originHeader.ToString();
        if (string.IsNullOrEmpty(value))
            return true;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsLoopback)
        {
            origin = value;
            return true;
        }

        return false;
    }

    private static async Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, McpJson.Wire)).ConfigureAwait(false);
    }
}
