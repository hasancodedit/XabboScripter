using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Xabbo.Scripter.Scripting;

namespace Xabbo.Scripter.Mcp;

public sealed record ScripterApiMember(string Name, string Kind, string Signature, string? Summary, bool IsAsync);

public sealed class ScripterApiCatalog
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly List<ScripterApiMember> _members;

    public ScripterApiCatalog()
    {
        Type type = typeof(G);
        IReadOnlyDictionary<string, string> summaries = LoadSummaries(type);
        _members = Build(type, summaries);
    }

    public IReadOnlyList<ScripterApiMember> Members => _members;

    public IEnumerable<ScripterApiMember> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _members;

        return _members.Where(m =>
            m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Signature.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (m.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static List<ScripterApiMember> Build(Type type, IReadOnlyDictionary<string, string> summaries)
    {
        List<ScripterApiMember> members = new();

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType != type) continue;

            string accessors = property.CanWrite ? "{ get; set; }" : "{ get; }";
            string signature = $"{ReflectionFormat.FriendlyName(property.PropertyType)} {property.Name} {accessors}";

            members.Add(new ScripterApiMember(property.Name, "property", signature, Lookup(summaries, "P:" + property.Name), false));
        }

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.DeclaringType != type || method.IsSpecialName) continue;

            string parameters = string.Join(", ", method.GetParameters().Select(ReflectionFormat.Parameter));
            string signature = $"{ReflectionFormat.FriendlyName(method.ReturnType)} {method.Name}({parameters})";

            members.Add(new ScripterApiMember(method.Name, "method", signature, Lookup(summaries, "M:" + method.Name), ReflectionFormat.IsAsync(method.ReturnType)));
        }

        return members
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Signature, StringComparer.Ordinal)
            .ToList();
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> summaries, string name) =>
        summaries.TryGetValue(name, out string? summary) ? summary : null;

    private static IReadOnlyDictionary<string, string> LoadSummaries(Type type)
    {
        Dictionary<string, string> summaries = new(StringComparer.Ordinal);

        foreach (string path in DocumentationPaths(type))
        {
            if (!File.Exists(path)) continue;

            try
            {
                XDocument document = XDocument.Load(path);

                foreach (XElement member in document.Descendants("member"))
                {
                    string? id = member.Attribute("name")?.Value;
                    XElement? summary = member.Element("summary");
                    if (id is null || summary is null || id.Length == 0) continue;

                    char kind = id[0];
                    if (kind != 'M' && kind != 'P') continue;

                    string name = SimpleName(id);
                    if (name.Length == 0) continue;

                    string key = $"{kind}:{name}";
                    if (summaries.ContainsKey(key)) continue;

                    summaries[key] = Whitespace.Replace(summary.Value, " ").Trim();
                }
            }
            catch { }

            if (summaries.Count > 0) break;
        }

        return summaries;
    }

    private static IEnumerable<string> DocumentationPaths(Type type)
    {
        string fileName = type.Assembly.GetName().Name + ".xml";

        string? location = type.Assembly.Location;
        if (!string.IsNullOrEmpty(location))
            yield return Path.ChangeExtension(location, ".xml");

        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string SimpleName(string documentationId)
    {
        int colon = documentationId.IndexOf(':');
        string body = colon >= 0 ? documentationId[(colon + 1)..] : documentationId;

        int parenthesis = body.IndexOf('(');
        if (parenthesis >= 0) body = body[..parenthesis];

        int generic = body.IndexOf('`');
        if (generic >= 0) body = body[..generic];

        int dot = body.LastIndexOf('.');
        return dot >= 0 ? body[(dot + 1)..] : body;
    }
}
