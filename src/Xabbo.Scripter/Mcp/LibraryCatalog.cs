using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Xabbo.Scripter.Scripting;

namespace Xabbo.Scripter.Mcp;

public sealed record LibraryInfo(string Name, string? Version, int TypeCount);

public sealed record LibraryTypeRef(string Name, string FullName, string Kind, string Assembly, string? Namespace, string? Summary);

public sealed record LibraryMember(string Kind, string Name, string Signature, bool IsStatic, string? Summary);

public sealed record LibraryTypeDetail(
    string Name,
    string FullName,
    string Kind,
    string Assembly,
    string? Namespace,
    string? Summary,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<LibraryMember> Members);

public sealed record LibraryMemberHit(string DeclaringType, string Kind, string Signature, bool IsStatic, string? Summary);

public sealed class LibraryCatalog
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly List<(Type Type, string Assembly)> _types = new();
    private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);
    private readonly List<LibraryInfo> _assemblies = new();

    private List<LibraryMemberHit>? _memberIndex;
    private readonly object _memberIndexLock = new();

    public LibraryCatalog()
    {
        foreach (Assembly assembly in DiscoverAssemblies())
        {
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch { continue; }

            string name = assembly.GetName().Name ?? assembly.FullName ?? "?";
            LoadSummaries(assembly);

            foreach (Type type in types)
            {
                if (type.IsSpecialName) continue;
                _types.Add((type, name));
            }

            _assemblies.Add(new LibraryInfo(name, assembly.GetName().Version?.ToString(), types.Length));
        }

        _assemblies.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    public IReadOnlyList<LibraryInfo> Assemblies => _assemblies;

    public IEnumerable<LibraryTypeRef> SearchTypes(string? query, string? assembly, int limit)
    {
        IEnumerable<(Type Type, string Assembly)> source = _types;

        if (!string.IsNullOrWhiteSpace(assembly))
            source = source.Where(t => t.Assembly.Contains(assembly, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
            source = source.Where(t =>
                t.Type.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (t.Type.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        return source
            .OrderBy(t => t.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(t => Ref(t.Type, t.Assembly));
    }

    public object GetType(string name)
    {
        List<(Type Type, string Assembly)> exact = _types
            .Where(t => string.Equals(t.Type.FullName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 0)
            exact = _types
                .Where(t => string.Equals(t.Type.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (exact.Count == 1)
            return Detail(exact[0].Type, exact[0].Assembly);

        if (exact.Count > 1)
            return new { ambiguous = true, candidates = exact.Select(t => Ref(t.Type, t.Assembly)).ToList() };

        List<LibraryTypeRef> suggestions = _types
            .Where(t => t.Type.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(t => Ref(t.Type, t.Assembly))
            .ToList();

        return new { notFound = true, query = name, suggestions };
    }

    public IEnumerable<LibraryMemberHit> SearchMembers(string query, string? kind, int limit)
    {
        IEnumerable<LibraryMemberHit> source = MemberIndex();

        if (!string.IsNullOrWhiteSpace(kind))
            source = source.Where(m => string.Equals(m.Kind, kind, StringComparison.OrdinalIgnoreCase));

        return source
            .Where(m => m.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType, StringComparer.OrdinalIgnoreCase)
            .Take(limit);
    }

    private List<LibraryMemberHit> MemberIndex()
    {
        if (_memberIndex is not null)
            return _memberIndex;

        lock (_memberIndexLock)
        {
            if (_memberIndex is not null)
                return _memberIndex;

            List<LibraryMemberHit> index = new();
            foreach ((Type type, _) in _types)
            {
                string declaring = FriendlyTypeName(type);
                foreach (LibraryMember member in MembersOf(type))
                    index.Add(new LibraryMemberHit(declaring, member.Kind, member.Signature, member.IsStatic, member.Summary));
            }

            return _memberIndex = index;
        }
    }

    private LibraryTypeRef Ref(Type type, string assembly) =>
        new(type.Name, type.FullName ?? type.Name, KindOf(type), assembly, type.Namespace, _summaries.GetValueOrDefault("T:" + TypeDocName(type)));

    private LibraryTypeDetail Detail(Type type, string assembly) =>
        new(
            type.Name,
            type.FullName ?? type.Name,
            KindOf(type),
            assembly,
            type.Namespace,
            _summaries.GetValueOrDefault("T:" + TypeDocName(type)),
            type.BaseType is { } baseType && baseType != typeof(object) ? ReflectionFormat.FriendlyName(baseType) : null,
            type.GetInterfaces().Select(ReflectionFormat.FriendlyName).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            MembersOf(type));

    private IReadOnlyList<LibraryMember> MembersOf(Type type)
    {
        if (type.IsEnum)
        {
            return Enum.GetNames(type)
                .Select(n => new LibraryMember("value", n, $"{n} = {Convert.ToInt64(Enum.Parse(type, n)):D}", true,
                    _summaries.GetValueOrDefault($"F:{TypeDocName(type)}.{n}")))
                .ToList();
        }

        List<LibraryMember> members = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (Type scope in TypeScope(type))
        {
            foreach (PropertyInfo property in scope.GetProperties(MemberFlags))
            {
                string accessors = property.CanWrite ? "{ get; set; }" : "{ get; }";
                string signature = $"{ReflectionFormat.FriendlyName(property.PropertyType)} {property.Name} {accessors}";
                Add(members, seen, "property", property.Name, signature, property.GetMethod?.IsStatic ?? false,
                    _summaries.GetValueOrDefault($"P:{TypeDocName(scope)}.{property.Name}"));
            }

            foreach (MethodInfo method in scope.GetMethods(MemberFlags))
            {
                if (method.IsSpecialName) continue;

                string parameters = string.Join(", ", method.GetParameters().Select(ReflectionFormat.Parameter));
                string signature = $"{ReflectionFormat.FriendlyName(method.ReturnType)} {method.Name}({parameters})";
                Add(members, seen, "method", method.Name, signature, method.IsStatic,
                    _summaries.GetValueOrDefault($"M:{TypeDocName(scope)}.{method.Name}"));
            }

            foreach (EventInfo @event in scope.GetEvents(MemberFlags))
            {
                string signature = $"event {ReflectionFormat.FriendlyName(@event.EventHandlerType!)} {@event.Name}";
                Add(members, seen, "event", @event.Name, signature, @event.AddMethod?.IsStatic ?? false,
                    _summaries.GetValueOrDefault($"E:{TypeDocName(scope)}.{@event.Name}"));
            }

            foreach (FieldInfo field in scope.GetFields(MemberFlags))
            {
                string prefix = field.IsLiteral ? "const " : field.IsStatic ? "static " : "";
                string signature = $"{prefix}{ReflectionFormat.FriendlyName(field.FieldType)} {field.Name}";
                Add(members, seen, "field", field.Name, signature, field.IsStatic,
                    _summaries.GetValueOrDefault($"F:{TypeDocName(scope)}.{field.Name}"));
            }
        }

        return members
            .OrderBy(m => m.Kind, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Add(List<LibraryMember> members, HashSet<string> seen, string kind, string name, string signature, bool isStatic, string? summary)
    {
        if (seen.Add(kind + " " + signature))
            members.Add(new LibraryMember(kind, name, signature, isStatic, summary));
    }

    private static IEnumerable<Type> TypeScope(Type type)
    {
        yield return type;

        if (type.IsInterface)
        {
            foreach (Type inherited in type.GetInterfaces())
                yield return inherited;
        }
    }

    private static string KindOf(Type type) =>
        type.IsEnum ? "enum" :
        type.IsInterface ? "interface" :
        type.IsValueType ? "struct" :
        typeof(Delegate).IsAssignableFrom(type) ? "delegate" :
        type.IsAbstract && type.IsSealed ? "static class" :
        "class";

    private static string FriendlyTypeName(Type type) =>
        type.Namespace is { Length: > 0 } ? $"{type.Namespace}.{ReflectionFormat.FriendlyName(type)}" : ReflectionFormat.FriendlyName(type);

    private static string TypeDocName(Type type)
    {
        Type definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        string full = (definition.FullName ?? definition.Name).Replace('+', '.');

        int bracket = full.IndexOf('[');
        return bracket >= 0 ? full[..bracket] : full;
    }

    private static IEnumerable<Assembly> DiscoverAssemblies()
    {
        HashSet<Assembly> assemblies = new()
        {
            typeof(G).Assembly,
            typeof(Xabbo.Core.IFloorItem).Assembly,
            typeof(Xabbo.ClientType).Assembly
        };

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if ((assembly.GetName().Name ?? "").StartsWith("Xabbo", StringComparison.Ordinal))
                assemblies.Add(assembly);
        }

        return assemblies;
    }

    private void LoadSummaries(Assembly assembly)
    {
        string? location = assembly.Location;
        if (string.IsNullOrEmpty(location))
            return;

        string path = Path.ChangeExtension(location, ".xml");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, (assembly.GetName().Name ?? "") + ".xml");
            if (!File.Exists(path))
                return;
        }

        try
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement member in document.Descendants("member"))
            {
                string? id = member.Attribute("name")?.Value;
                XElement? summary = member.Element("summary");
                if (id is null || summary is null || id.Length < 2 || id[1] != ':')
                    continue;

                string key = MemberKey(id);
                if (_summaries.ContainsKey(key))
                    continue;

                _summaries[key] = Whitespace.Replace(summary.Value, " ").Trim();
            }
        }
        catch { }
    }

    private static string MemberKey(string documentationId)
    {
        if (documentationId[0] != 'M')
            return documentationId;

        string body = documentationId;

        int parenthesis = body.IndexOf('(');
        if (parenthesis >= 0) body = body[..parenthesis];

        int methodGeneric = body.IndexOf("``", StringComparison.Ordinal);
        if (methodGeneric >= 0) body = body[..methodGeneric];

        return body;
    }
}
