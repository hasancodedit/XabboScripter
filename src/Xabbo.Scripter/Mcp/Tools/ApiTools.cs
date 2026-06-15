using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.Extensions.Options;

using Xabbo.Scripter.Engine;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class ApiTools : IMcpToolProvider
{
    private const string Guide =
@"# Writing xabbo scripter scripts

Scripts are C# script files (.csx) made of top-level statements. Every public member of the
globals class 'G' is directly in scope, so you call the game API without any prefix.

## Essentials
- Calls are made directly: `Send(Out.Chat, ""hello"");`, `await Delay(500);`, `var room = Room;`.
- Most actions are async — use `await`. Long operations accept timeouts.
- `Ct` is the script's CancellationToken. Pass it to your own awaits and loops so the Stop
  button can cancel the script: `while (!Ct.IsCancellationRequested) { ... }`.
- A non-null value returned (or left as the last expression) is formatted into the log.
- Throwing an exception marks the script as faulted and prints the error + line to the log.

## Metadata directives (optional, as the first lines)
- `/// @name My Script`  sets the display name / tab title.
- `/// @group Utilities` groups the script in the script list.

## Discovering the API
- `list_api` returns every callable member signature (the full surface).
- `get_api` with a search term returns matching members with their documentation.
- `get_imports` lists the default usings and referenced assemblies available to scripts.

## Examples
```csharp
/// @name Wave
Wave();
```
```csharp
/// @name Greet room
foreach (var user in Users)
    Send(Out.Chat, $""Hello {user.Name}!"");
```
```csharp
/// @name Walk loop
while (!Ct.IsCancellationRequested)
{
    await WalkTo(5, 5);
    await WalkTo(6, 6);
}
```";

    private const string Started =
@"This MCP server drives the xabbo scripter. A typical workflow:

1. `get_server_info` — confirm the scripter is connected to the game (canExecute).
2. `get_knowledgebase` — read the full field guide once: the API by domain, packets/events,
   data models, proven recipes, a debugging playbook and a cheat sheet.
3. `get_scripting_guide` — the short syntax primer, if you only need the basics.
4. `list_api` / `get_api` — discover or verify the exact game functions you can call.
4. Inspect context: `get_room`, `get_self`, `list_scripts`, `list_tabs`.
5. Author: `create_script_tab` opens a visible editor tab with your code so the user can watch it,
   or `run_code` runs code in the background to gather information.
6. Run & debug: `run_script` / `run_code`, then `get_script_status` and `get_errors` to read
   compile/runtime errors. Use `edit_tab` to live-fix an open tab and re-run it.
7. Persist: `save_script` writes a script to disk; `add_autostart` runs it automatically on connect.

Call `list_mcp_tools` to see every tool and its parameters.";

    private static string? _knowledgebase;

    private readonly ScripterApiCatalog _catalog;
    private readonly IOptions<ScriptEngineOptions> _options;

    public ApiTools(ScripterApiCatalog catalog, IOptions<ScriptEngineOptions> options)
    {
        _catalog = catalog;
        _options = options;
    }

    [McpTool("get_started", "Get an orientation overview of this MCP server and the recommended workflow for creating, running and debugging scripts.")]
    public string GetStarted() => Started;

    [McpTool("get_scripting_guide", "Get a guide explaining how to write xabbo scripter scripts (syntax, async, the cancellation token, metadata directives and examples).")]
    public string GetScriptingGuide() => Guide;

    [McpTool("get_knowledgebase", "Get the full xabbo scripter knowledgebase: a dense field guide (the API by domain, packets/headers/interception, events, data models, proven recipes mined from real scripts, a debugging playbook and a cheat sheet) distilled from the entire source. Read this once to understand the whole system before writing scripts.")]
    public string GetKnowledgebase()
    {
        if (_knowledgebase is not null)
            return _knowledgebase;

        Assembly assembly = typeof(ApiTools).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Knowledgebase.md", StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return "Knowledgebase resource not found.";

        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            return "Knowledgebase resource not found.";

        using StreamReader reader = new(stream);
        return _knowledgebase = reader.ReadToEnd();
    }

    [McpTool("list_api", "List every member of the scripting API (the globals available to scripts) as a compact index of signatures.")]
    public object ListApi()
    {
        return new
        {
            count = _catalog.Members.Count,
            members = _catalog.Members.Select(m => m.Signature).ToList()
        };
    }

    [McpTool("get_api", "Get detailed scripting API members (signature plus documentation) optionally filtered by a search term that matches the name, signature or documentation.")]
    public object GetApi(
        [McpParameter("Optional case-insensitive search term. Omit to return the entire API.")] string? search = null)
    {
        List<object> members = _catalog.Search(search)
            .Select(m => (object)new { m.Name, m.Kind, m.Signature, m.Summary, m.IsAsync })
            .ToList();

        return new { count = members.Count, members };
    }

    [McpTool("get_imports", "List the default namespace imports and referenced assemblies available to every script.")]
    public object GetImports()
    {
        return new
        {
            imports = _options.Value.Imports ?? new List<string>(),
            references = _options.Value.References ?? new List<string>()
        };
    }
}
