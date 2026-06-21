using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Xabbo.Scripter.Mcp;

internal static class ReflectionFormat
{
    public static string Parameter(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        string modifier = parameter.IsOut ? "out " : type.IsByRef ? "ref " : "";
        if (type.IsByRef)
            type = type.GetElementType()!;

        string text = $"{modifier}{FriendlyName(type)} {parameter.Name}";
        return parameter.HasDefaultValue ? $"{text} = {Default(parameter.DefaultValue)}" : text;
    }

    public static string Default(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "null"
    };

    public static bool IsAsync(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
            return true;

        if (returnType.IsGenericType)
        {
            Type definition = returnType.GetGenericTypeDefinition();
            return definition == typeof(Task<>) || definition == typeof(ValueTask<>);
        }

        return false;
    }

    public static string FriendlyName(Type type)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return FriendlyName(nullable) + "?";

        if (type.IsByRef)
            return FriendlyName(type.GetElementType()!);

        if (type.IsArray)
            return FriendlyName(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            string name = type.Name;
            int backtick = name.IndexOf('`');
            if (backtick >= 0) name = name[..backtick];

            string arguments = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
            return $"{name}<{arguments}>";
        }

        return type switch
        {
            _ when type == typeof(void) => "void",
            _ when type == typeof(string) => "string",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(short) => "short",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(double) => "double",
            _ when type == typeof(float) => "float",
            _ when type == typeof(object) => "object",
            _ => type.Name
        };
    }
}
