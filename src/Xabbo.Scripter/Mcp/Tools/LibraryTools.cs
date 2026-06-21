using System.Linq;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class LibraryTools : IMcpToolProvider
{
    private readonly LibraryCatalog _catalog;

    public LibraryTools(LibraryCatalog catalog)
    {
        _catalog = catalog;
    }

    [McpTool("list_libraries", "List the xabbo libraries available for introspection (Xabbo.Core, Xabbo.Common, Xabbo.GEarth, etc.) with their version and exported type count. Use this to see what you can search.")]
    public object ListLibraries() => new { libraries = _catalog.Assemblies };

    [McpTool("search_types", "Search every type across the xabbo libraries by name (interfaces, classes, structs, enums, delegates). Returns matching types with their kind, namespace, assembly and doc summary. Use this to discover the types behind the values a script receives (IFloorItem, IRoomUser, FurniData, ...).")]
    public object SearchTypes(
        [McpParameter("Case-insensitive term matched against the type name and full name. Omit to list everything (capped by limit).")] string? query = null,
        [McpParameter("Optional assembly name filter, e.g. \"Xabbo.Core\".")] string? assembly = null,
        [McpParameter("Maximum number of results (default 50).")] int limit = 50)
    {
        var types = _catalog.SearchTypes(query, assembly, limit <= 0 ? 50 : limit).ToList();
        return new { count = types.Count, types };
    }

    [McpTool("get_type", "Get the full definition of a library type: its kind, base type, implemented interfaces, doc summary and EVERY member (properties, methods with full signatures, events, fields, or enum values) each with its own summary. Accepts a simple name (IFloorItem) or full name (Xabbo.Core.IFloorItem). This is how you learn all methods and options a class exposes.")]
    public object GetType(
        [McpParameter("The type name, simple (IFloorItem) or fully qualified (Xabbo.Core.IFloorItem).")] string name)
        => _catalog.GetType(name);

    [McpTool("search_members", "Search members (methods, properties, events, fields, enum values) by name across every type in the xabbo libraries. Returns each match with its declaring type, signature and summary. Use this to find which type exposes a capability, e.g. search \"Walk\", \"Trade\" or \"Parse\".")]
    public object SearchMembers(
        [McpParameter("Case-insensitive term matched against the member signature.")] string query,
        [McpParameter("Optional kind filter: property, method, event, field or value.")] string? kind = null,
        [McpParameter("Maximum number of results (default 60).")] int limit = 60)
    {
        var members = _catalog.SearchMembers(query, kind, limit <= 0 ? 60 : limit).ToList();
        return new { count = members.Count, members };
    }
}
