This MCP server drives the xabbo scripter. A typical workflow:

1. `get_server_info` — confirm the scripter is connected to the game (canExecute).
2. `get_knowledgebase` — read the full field guide once: the API by domain, packets/events,
   data models, proven recipes, a debugging playbook and a cheat sheet.
3. `get_scripting_guide` — the short syntax primer, if you only need the basics.
4. `list_api` / `get_api` — discover or verify the exact game functions (the `G` API) you can call.
   `list_libraries` / `search_types` / `get_type` / `search_members` — deep-introspect the xabbo
   libraries themselves: every type, method, property and enum (with doc summaries) behind the
   objects your scripts receive (`IFloorItem`, `IRoomUser`, `FurniData`, ...).
5. Inspect context: `get_room`, `get_self`, `list_scripts`, `list_tabs`.
6. Author: `create_script_tab` opens a visible editor tab with your code so the user can watch it,
   or `run_code` runs code in the background to gather information.
7. Run & debug: `run_script` / `run_code`, then `get_script_status` and `get_errors` to read
   compile/runtime errors. Use `edit_tab` to live-fix an open tab and re-run it.
8. Persist: `save_script` writes a script to disk; `add_autostart` runs it automatically on connect.

Call `list_mcp_tools` to see every tool and its parameters.
