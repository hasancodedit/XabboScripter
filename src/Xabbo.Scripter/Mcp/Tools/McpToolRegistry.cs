using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Xabbo.Scripter.Mcp.Protocol;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

public sealed class McpToolRegistry
{
    private sealed record ParamMeta(ParameterInfo Parameter, bool IsNullable, bool IsCancellationToken)
    {
        public string Name => Parameter.Name!;
        public bool Required => !IsCancellationToken && !IsNullable && !Parameter.HasDefaultValue;
    }

    private sealed record ToolBinding(object Target, MethodInfo Method, ParamMeta[] Parameters, Tool Descriptor);

    private readonly Dictionary<string, ToolBinding> _bindings = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<Tool> _descriptors;

    public McpToolRegistry(IEnumerable<IMcpToolProvider> providers)
    {
        NullabilityInfoContext nullability = new();

        foreach (IMcpToolProvider provider in providers)
        {
            foreach (MethodInfo method in provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                McpToolAttribute? attribute = method.GetCustomAttribute<McpToolAttribute>();
                if (attribute is null) continue;

                ParamMeta[] parameters = method.GetParameters()
                    .Select(p => new ParamMeta(p, IsNullable(p, nullability), p.ParameterType == typeof(CancellationToken)))
                    .ToArray();

                Tool descriptor = new()
                {
                    Name = attribute.Name,
                    Description = attribute.Description,
                    InputSchema = BuildSchema(parameters)
                };

                _bindings[attribute.Name] = new ToolBinding(provider, method, parameters, descriptor);
            }
        }

        _descriptors = _bindings.Values
            .Select(b => b.Descriptor)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<Tool> Tools => _descriptors;

    public async Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!_bindings.TryGetValue(name, out ToolBinding? binding))
            return CallToolResult.Failure($"Unknown tool '{name}'.");

        try
        {
            object?[] args = BindArguments(binding.Parameters, arguments, cancellationToken);
            object? result = binding.Method.Invoke(binding.Target, args);
            result = await Unwrap(result).ConfigureAwait(false);
            return Convert(result);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException oce)
        {
            throw oce;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return CallToolResult.Failure(Describe(ex.InnerException));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CallToolResult.Failure(Describe(ex));
        }
    }

    private static object?[] BindArguments(ParamMeta[] parameters, JsonElement arguments, CancellationToken cancellationToken)
    {
        object?[] values = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParamMeta parameter = parameters[i];

            if (parameter.IsCancellationToken)
            {
                values[i] = cancellationToken;
                continue;
            }

            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty(parameter.Name, out JsonElement value) &&
                value.ValueKind != JsonValueKind.Null)
            {
                values[i] = value.Deserialize(parameter.Parameter.ParameterType, McpJson.Wire);
            }
            else if (parameter.Parameter.HasDefaultValue)
            {
                values[i] = parameter.Parameter.DefaultValue;
            }
            else if (parameter.IsNullable)
            {
                values[i] = null;
            }
            else
            {
                throw new McpToolException($"Missing required parameter '{parameter.Name}'.");
            }
        }

        return values;
    }

    private static async Task<object?> Unwrap(object? result)
    {
        if (result is Task task)
        {
            await task.ConfigureAwait(false);

            Type type = task.GetType();
            if (type.IsGenericType)
                return type.GetProperty(nameof(Task<object>.Result))!.GetValue(task);

            return null;
        }

        return result;
    }

    private static CallToolResult Convert(object? result)
    {
        return result switch
        {
            null => CallToolResult.Text("(no output)"),
            CallToolResult callToolResult => callToolResult,
            string text => CallToolResult.Text(text),
            _ => CallToolResult.Text(JsonSerializer.Serialize(result, McpJson.Result))
        };
    }

    private static object BuildSchema(ParamMeta[] parameters)
    {
        Dictionary<string, object> properties = new();
        List<string> required = new();

        foreach (ParamMeta parameter in parameters)
        {
            if (parameter.IsCancellationToken) continue;

            Dictionary<string, object> schema = new() { ["type"] = JsonTypeOf(parameter.Parameter.ParameterType) };

            McpParameterAttribute? description = parameter.Parameter.GetCustomAttribute<McpParameterAttribute>();
            if (description is not null)
                schema["description"] = description.Description;

            properties[parameter.Name] = schema;

            if (parameter.Required)
                required.Add(parameter.Name);
        }

        Dictionary<string, object> root = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required.Count > 0)
            root["required"] = required;

        return root;
    }

    private static string JsonTypeOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(bool)) return "boolean";
        if (type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong)) return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";

        return "string";
    }

    private static bool IsNullable(ParameterInfo parameter, NullabilityInfoContext context)
    {
        Type type = parameter.ParameterType;

        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) is not null;

        return context.Create(parameter).WriteState == NullabilityState.Nullable;
    }

    private static string Describe(Exception exception) =>
        exception is McpToolException ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";
}
