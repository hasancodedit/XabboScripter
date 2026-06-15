# Xabbo Scripter — MCP Knowledgebase

> The complete field guide for an AI driving the xabbo scripter via MCP. Generated from a full read of the scripter source (the G API, Xabbo.Core, Xabbo.Common, the engine, the MCP server) and 213 real user scripts. When in doubt, verify a member with `get_api <term>`, confirm a packet header resolves before sending it (an unknown `Out[..]`/`In[..]` identifier throws `UnknownHeaderException` — several recipe header names below are illustrative and should be checked against the live header set), and inspect live state with `get_room`/`get_self`.

## Contents
- Orientation & mental model
- Execution & lifecycle model
- MCP tool reference
- Scripting API reference (by domain)
- Packets, headers & interception
- Events
- Data models reference
- Recipes & proven patterns
- Debugging & issue-spotting playbook
- Cheat sheet & gotchas

## Orientation & mental model

**What it is.** Xabbo Scripter runs C# `.csx` scripts that drive a Habbo client through an interceptor. You author scripts via MCP tools; each script is compiled with Roslyn and executed against a per-run instance of the globals class **`G`**. Every `public` member of `G` is in scope with **no prefix** — call `Send(...)`, `Move(...)`, read `Room`, etc. directly.

**Execution model (what actually happens per run):**

| Stage | Behavior |
|---|---|
| Compile | Roslyn CSharp scripting, `LanguageVersion.Latest`, `globalsType: typeof(G)`. `#load` resolves relative to `%APPDATA%/xabbo/scripter/scripts`. Errors → `CompileError`, reported as `(line,col): error CSxxxx: msg`. |
| Run | Synchronous on a thread-pool thread. Fresh `new G(Host, script)` in a `using` (`G : IDisposable`). |
| Result | Non-null last expression / returned value is formatted and logged. |
| Faults | Thrown exception → `RuntimeError` (line shown as `filename:line N`; `Xabbo.*` frames filtered). `OperationCanceledException` + cancellation requested → `Canceled`; without → `TimedOut`. |
| Cleanup | On dispose, all `OnIntercept`/`On*` registrations are auto-removed. |

`ScriptStatus`: `None / Compiling / Running / Cancelling / Canceled / Complete / CompileError / RuntimeError / TimedOut / UnknownError / FileNotFound / Aborted`.

**Mental model — the core abstractions:**

- **`Ct`** (`CancellationToken`) — the script's lifeline. `Run` is exactly `!Ct.IsCancellationRequested`. Use `while (Run)` for loops.
- **`Delay(ms)`** is the canonical cancellation/exit point: it throws `OperationCanceledException` on cancel, which the engine catches and reports cleanly. Same for `Wait()` (`Delay(-1)`, blocks forever) and blocking `Receive`.
- **`Finish()`** = voluntary clean stop: sets `IsFinished = true`, cancels `Ct`, then throws immediately — code after it never runs. Call it from a callback to end the script.
- **`Error("msg")`** *returns* a `ScriptException`; you must `throw Error("msg")` to fault with a clean message.
- **`In` / `Out`** are header collections — `Out.Chat`, `In.Whisper`, etc. Sending on an `In.*` header injects toward the client (cosmetic); `Out.*` goes to the server.
- **`Global`** (`dynamic`) — a bag shared across all scripts in the session; persists between runs. Use `InitGlobal(name, value)` to seed it race-safely.

**Directives** (triple-slash comments, scanned by regex before execution; put them on the first lines):

```
/// @name My Script
/// @group Automation
```

`@name` sets the tab title; `@group` groups it in the list.

**Default imports** (no `using` needed): `System`, `System.Text`, `System.Text.RegularExpressions`, `System.IO`, `System.Linq`, `System.Collections`, `System.Collections.Generic`, `System.Threading.Tasks`, `Xabbo`, `Xabbo.Messages`, `Xabbo.Interceptor`, `Xabbo.Core`, `Xabbo.Core.Extensions`, `Xabbo.Scripter.Runtime`, `Xabbo.Scripter.Runtime.PacketTypes`. Nullable is **disabled**.

**API discovery loop** (use these MCP tools, then consult this doc):

| Tool | Returns |
|---|---|
| `list_api` | All `G` member signatures (the full surface). |
| `get_api <term>` | Matching members with docs. |
| `get_imports` | Default `using`s + referenced assemblies. |

Workflow: skim `list_api` for the verb you need → `get_api <term>` for exact signature/overloads/defaults → write → run → read the log/status → fix the reported `filename:line`.

**Fastest path from zero:** write top-level statements that call `G` members directly; return or log a value to see output; throw to fault. No class, no `Main`, no namespace.

**Canonical minimal script:**

```csharp
/// @name Hello
Talk("hello world");
```

**Slightly richer example** — wait for a room, greet each user, then stay alive intercepting chat until cancelled:

```csharp
/// @name Greeter
/// @group Demo

if (!IsInRoom) {
    Log("Not in a room — waiting for entry...");
    OnEnteredRoom(e => Log($"Entered room {RoomId}"));
}

foreach (var user in Users) {
    if (!Run) break;
    Whisper(user, "hi from a script");
    Delay(500);
}

OnIntercept(Out.Chat, e => {
    string msg = e.Packet.ReadString();
    if (msg == "!stop") {
        e.Block();
        Finish();
    } else {
        Log($"chat: {msg}");
    }
});

Wait();
```

Key habits: loop on `while (Run)`/check `!Run` to break; `Delay()` between actions (and as the exit point inside `RunTask`); register `On*`/`OnIntercept` handlers *before* `Wait()`; remember callbacks fire on the interceptor thread, so call `Finish()` there to unblock the waiting script thread.

## Execution & lifecycle model

Every `.csx` runs as a Roslyn script compiled with `globalsType = typeof(G)`, so all public `G` members are in scope unqualified. A fresh `G` instance is built per run (`new G(Host, script)` in a `using`) and disposed when the script ends, which auto-removes every `OnIntercept` / `On*` registration. The script body executes synchronously on a thread-pool thread via `.GetAwaiter().GetResult()`.

### Status state machine

`ScriptStatus`: `None` → `Compiling` → `Running` → (`Cancelling`) → terminal. Terminal values and how they arise:

| Status | Cause |
|---|---|
| `Complete` | Body returned normally. |
| `Canceled` | `OperationCanceledException` thrown **and** `Ct.IsCancellationRequested` (external Stop or `Finish()`). |
| `TimedOut` | `OperationCanceledException` thrown **without** cancellation requested (a timeout fired). |
| `RuntimeError` | Any other exception; `IsFaulted = true`. |
| `CompileError` | Roslyn diagnostics; reported as `(line,col): error CSxxxx: message`. |
| `Aborted` / `FileNotFound` / `UnknownError` | Engine-level failures (no source / hard abort). |

### Cancellation: `Ct`, `Run`, Stop, `Finish()`

The single source of truth for "should I keep going" is the script `CancellationToken Ct` (linked to script-level + host-level CTS).

| Member | Type | Use |
|---|---|---|
| `Ct` | `CancellationToken` | Pass to every async/cancelable call you make yourself. |
| `Run` | `bool` | `!Ct.IsCancellationRequested`. The canonical loop guard. |
| `IsFinished` | `bool` | `true` only after `Finish()` (distinguishes voluntary stop from external Stop). |
| `Finish()` | `void` | Cancels own CTS, sets `IsFinished = true`, then throws `OperationCanceledException` at the call site — clean stop from inside a callback. |
| `Error(string)` | `ScriptException` | Returns (does not throw) an exception; `throw Error("msg")` to fault with a clean log message and no stack trace. |

- **Stop button / host shutdown** sets `Ct.IsCancellationRequested`. You observe it by: checking `Run`, or letting a blocking call (`Delay`, `Wait`, `Receive`) throw `OperationCanceledException`. Let that exception propagate — the engine classifies it as `Canceled` (do not catch and swallow it).
- `Finish()` and external Stop both surface as `OperationCanceledException`; `IsFinished` is the only way to tell them apart after the fact.
- Cooperative cancellation only: a tight CPU loop with no `Delay`/`Run` check will ignore Stop until it hits a cancelation point.

### Delays & keeping alive

| Member | Signature | Notes |
|---|---|---|
| `Delay` | `void Delay(int ms)` / `void Delay(TimeSpan)` | Blocking sleep; throws `OperationCanceledException` on cancel — the natural loop exit point. |
| `DelayAsync` | `Task DelayAsync(int ms)` / `Task DelayAsync(TimeSpan)` | Awaitable form. |
| `Wait` | `void Wait()` | `Delay(-1)` — blocks forever until cancelled. Use to keep event/intercept-driven scripts alive. |
| `RunTask` | `void RunTask(Action)` | Queues work on the thread pool. The action must poll `Run` and use `Delay` as exit points or it outlives the script. |

```csharp
while (Run) {
    Send(Out.Wave);
    Delay(5000);
}
```

Event-driven pattern: register handlers, then park the thread. `Finish()` from a callback unblocks `Wait()`.

```csharp
OnIntercept(Out.Chat, e => {
    string msg = e.Packet.ReadString();
    if (msg == "!stop") { e.Block(); Finish(); }
});
Wait();
```

### Output: return value & logging

- A **non-null returned value** is formatted (via `ObjectFormatter`) and appended to the output log — the last expression doubles as a result printout.
- `Log(string)` / `Log(object?)` / `Log()` append explicit lines. `Status(string?/object?)` updates the status-bar text, not the log.
- On `RuntimeError`, the message and stack trace are logged, but frames in `Xabbo.*` namespaces are stripped — only your script frames remain, shown as `filename:line N`. Throwing `Error("...")` / `ScriptException` yields a clean message with no trace.

```csharp
Send(Out.GetCredits);
var pkt = Receive(In.Credits, timeout: 5000);
return pkt.ReadInt();   // logged as the result
```

### Threading

- The script body and `Delay`/`Wait`/`Receive` run on the script's thread-pool thread.
- **`OnIntercept` and `On*` callbacks fire on the interceptor's thread, not the script thread.** Shared mutable state touched from both needs synchronization; calling `Finish()` from a callback is safe and unblocks the script thread.
- `RunTask(Action)` adds more thread-pool threads — same cancellation discipline applies.
- WPF/UI work must go through `InvokeOnUiThread<T>(Func<T>)` (marked `[EditorBrowsable(Never)]`); scripts rarely need it.

### State that is `null` before entering a room

These are `null`/sentinel until the room is fully loaded (`IsInRoom == true`). Room-scoped action methods call an internal `RequireRoom()` and throw `InvalidOperationException("The user is not in a room.")` when called too early.

| Member | Pre-room value |
|---|---|
| `Room`, `DoorTile`, `Heightmap`, `FloorPlan` | `null` |
| `Self` (`GetUserById(UserId)`) | `null` |
| `RoomId` | `-1` |
| `IsInRoom` | `false` |
| `Entities` / `Users` / `Pets` / `Bots` | empty sequence |

```csharp
if (!IsInRoom) {
    var r = EnsureEnterRoom(roomId);
    if (r != RoomEntryResult.Success) throw Error($"enter failed: {r}");
}
foreach (var u in Users) Log(u.Name);
```

`EnsureEnterRoom` blocks until loaded and returns a `RoomEntryResult` (`Success` / `Full` / `Banned` / `Unknown` — the `InvalidPassword` enum value exists but the entry task does not currently produce it; a wrong password resolves to `Unknown`); `EnterRoom` is fire-and-forget with no confirmation, so guard subsequent room access with `IsInRoom` or an `OnEnteredRoom` handler.

### Hidden background runs vs visible editor tabs

| | Visible editor tab | Hidden background run (`run_code` / `run_script` with `wait=false`) |
|---|---|---|
| Trigger | User runs a `.csx` from the editor UI. | MCP tool starts the script detached. |
| Caller blocking | Engine runs the body to completion as above. | Tool returns immediately; script keeps running until it finishes, faults, or is stopped. |
| Output visibility | Log/Status surface in that tab. | No tab; the caller does not receive return value/log inline. Use `Log`/`Status` and poll status, or run with `wait=true` to get the result back. |
| Lifecycle | Same `G` lifecycle, `Ct`, auto-deregistration. | Identical lifecycle; differs only in who waits and where output goes. |

Practical consequences for a background run: if the body finishes synchronously the script ends immediately (any `OnIntercept`/`On*` handlers are torn down with it), so a background script that must keep reacting to packets has to end with `Wait()` (or a `while (Run)` loop). Conversely, when you want a one-shot result back inline, run with `wait=true` and `return` the value.

I have full ground truth on every tool, parameter names/defaults, return shapes, and guard requirements. Writing the section now.

## MCP tool reference

All tools are MCP functions that drive the running scripter app. Scripts themselves are C# `.csx` top-level statements where every public member of globals class `G` is in scope (e.g. `Send(Out.Chat,"hi")`, `await Delay(500)`, `var room = Room`). These tools author/run/inspect those scripts; they do **not** run inside a script.

### Discovery & meta (read these first, no permissions needed)

| Tool | Params | When to use |
|---|---|---|
| `get_started` | – | Orientation + recommended workflow. One read at session start. |
| `get_scripting_guide` | – | Short syntax primer: async, `Ct`, `/// @name`/`/// @group` directives, examples. |
| `get_knowledgebase` | – | Full dense field guide (API by domain, packets/events, models, recipes, debug playbook). Read once before authoring. |
| `list_api` | – | Compact index of **every** `G` member signature. The full callable surface. |
| `get_api` | `search?` | Member detail: `Name, Kind, Signature, Summary, IsAsync`. Omit `search` for everything; pass a term (matches name/signature/docs) to filter. Verify exact names here before writing. |
| `get_imports` | – | Default `using`s and referenced assemblies available to scripts. |
| `list_mcp_tools` | – | Every MCP tool with description + input schema (self-discovery). |
| `get_server_info` | – | Server state: `running, endpoint, requestCount, sessionCount, toolCount, scripterConnected`, `permissions{execute,fileWrite,editor}`, `authRequired`. Check before any run. |
| `get_integration` | – | Config snippets/CLI for wiring external LLM clients. Rarely needed mid-task. |

### Connection & game context (read-only, no permissions)

| Tool | Params | Returns / when to use |
|---|---|---|
| `get_connection` | – | `connected, client, clientIdentifier, clientVersion, hotel, inRoom, roomId, inQueue, ringingDoorbell`. Cheap readiness check. |
| `get_room` | `maxFurni?=200` | Full room snapshot: `room{…}, rights{isOwner,hasRights,…}, self, users[], pets[], floorItems[], wallItems[]` (furni capped at `maxFurni` each, with true `*Count`). Primary state inspector. Returns `{inRoom:false}` if not in a room. |
| `get_self` | – | Own avatar: `id,index,name,figure,position{x,y,z},direction,isIdle,isTyping,dance,effect,hasRights,…`. Throws if not in room. |
| `get_profile` | – | Account: `userData{id,name,figure,gender,motto}, credits, diamonds, duckets, achievementScore, homeRoom`. |
| `get_inventory` | – | `{loaded, count}` summary only. |
| `get_errors` | `script?` | With `script`: that script's `status,faulted,error,output`. Without: all scripts in error/faulted state `{count, errors[]}`. Go-to after a run. |

### Script catalog (read-only)

| Tool | Params | When to use |
|---|---|---|
| `list_scripts` | – | Every known script (disk + unsaved tabs): `name,group,fileName,status,…`. |
| `search_scripts` | `query` | Case-insensitive match over name/group/**code**; returns matches with full code. Find prior art / reuse. |
| `get_script` | `script` | Full source + status of one script (by file name ±`.csx` or display name). Auto-loads from disk. |
| `get_script_status` | `script` | Run state: `status, running, working, faulted, runtimeMs, output, error`. |
| `get_script_log` | `script` | `fileName, status, faulted, output` (the text under the editor). |

### Editor tabs (live UI; requires `editor` permission for mutating ops)

| Tool | Params | Permission | When to use |
|---|---|---|---|
| `list_tabs` | – | none | Open tabs in order: `index, active, name, fileName, modified, running, status`. |
| `get_active_tab` | – | none | Active tab + full code. Throws if none active. |
| `open_script` | `script` | editor | Open existing script in a visible tab and switch to it. |
| `create_script_tab` | `code` | editor | New unsaved script in a visible tab the user can watch. |
| `edit_tab` | `script, code` | editor | Replace an open tab's code **live** (opens it first if needed). Core of live-fix loop. |
| `select_tab` | `script` | editor | Switch active tab (opening if needed). |
| `close_tab` | `script` | editor | Close a tab. |

### Execution (requires `execute` permission + `scripterConnected`)

| Tool | Params | When to use |
|---|---|---|
| `run_code` | `code, visible?=false, wait?=true, timeoutMs?=30000` | Ad-hoc code. `visible=false` runs **hidden** — ideal for inspecting state and quick probes. `visible=true` shows a live tab. Returns the run snapshot (`status,faulted,runtimeMs,output,error,note`). |
| `run_script` | `script, wait?=true, timeoutMs?=30000` | Compile+run an existing saved/open script. `wait=false` starts and returns `{note:"started"}`. Returns `{note:"already running"}` if already working. |
| `cancel_script` | `script` | Cancel a running/compiling script (fires its `Ct`). |

Notes: when `wait=true` and the script outlasts `timeoutMs`, you get `note:"timed out waiting; still running"` (not an error) — poll with `get_script_status`. If not connected, run tools throw and tell you to check `get_server_info`.

### Files (requires `fileWrite` permission)

| Tool | Params | When to use |
|---|---|---|
| `save_script` | `fileName, code, overwrite?=false` | Persist to disk (creates or, with `overwrite=true`, replaces). Updates the live tab if open. Errors on existing file/name unless `overwrite`. Returns `{saved, path, script}`. |
| `rename_script` | `script, newFileName` | Rename file on disk. Fails if running or target name clashes. |
| `delete_script` | `script` | Delete from disk + remove from scripter. Fails if running. |

### Autostart (run on connect; mutating ops require `fileWrite`)

| Tool | Params | When to use |
|---|---|---|
| `list_autostart` | – | Configured autostart tasks: `name,fileName,status,running,valid,addedAt`. |
| `add_autostart` | `script` | Mark a **saved** script to run on connect (save it first). |
| `remove_autostart` | `script` | Unmark. |
| `restart_autostart` | `script` | Stop-if-running then run again now. |
| `stop_autostart` | `script` | Stop a running autostart task. |

### App log

| Tool | Params | When to use |
|---|---|---|
| `get_app_log` | `maxChars?=8000` | Engine/connection/error messages (tail). Use when a failure is outside a specific script (connection drop, engine error). |

### Recommended end-to-end loop

1. **Confirm connection** — `get_server_info` (check `scripterConnected` + `permissions`). Fallback detail: `get_connection`.
2. **Learn the API** (first session) — `get_knowledgebase`, then `list_api` / `get_api <term>` to verify exact signatures. Never guess member names.
3. **Inspect context** — `get_room` / `get_self` / `get_profile`; `search_scripts` to reuse existing code.
4. **Probe before committing** — `run_code` with `visible=false` to read live state without cluttering the UI:
   ```csharp
   /// @name probe
   var others = Users.Where(u => u.Id != Self.Id).Select(u => u.Name).ToArray();
   $"{others.Length} others: {string.Join(", ", others)}"
   ```
5. **Author** — `create_script_tab` to show the user a live tab, or keep iterating via `run_code`. Use directives + honor `Ct` in loops:
   ```csharp
   /// @name follow
   /// @group movement
   while (!Ct.IsCancellationRequested)
   {
       var t = Users.FirstOrDefault(u => u.Name == "alice");
       if (t is not null) await WalkTo(t.X, t.Y);
       await Delay(500);
   }
   ```
6. **Run** — `run_script` / `run_code`. For long-running loops use `wait:false` (or accept the `timeoutMs` poll note) so the call returns.
7. **Read errors** — inspect the run snapshot's `error`, then `get_errors <script>` / `get_script_status` for the compile/runtime message + faulting line. `get_app_log` for engine/connection issues.
8. **Live-edit** — `edit_tab <script,newCode>` to patch the open tab in place, then re-`run_script`. No save needed between iterations.
9. **Persist** — `save_script` once correct; `add_autostart` if it should run on connect.

### Fast-debug tool combos

- **Tight fix loop:** `run_script` → `get_errors <script>` (faulting line + message) → `edit_tab` → `run_script`. Repeat without touching disk.
- **State recon without UI noise:** `run_code(visible:false, wait:true)` returning a last-expression value; the value is formatted into `output`.
- **Background task supervision:** `run_script(wait:false)` → poll `get_script_status` → `cancel_script` to stop. `working` true while compiling/running; `faulted` flags errors; `runtimeMs` shows elapsed.
- **"Nothing ran" / connection faults:** `get_server_info` (`scripterConnected`, `permissions.execute`) + `get_app_log` rather than per-script logs.
- **Permission-blocked tool calls:** mutating editor/file/run tools throw if the matching permission (`editor` / `fileWrite` / `execute`) is off — confirm via `get_server_info.permissions` before relying on them.

## Scripting API reference (by domain)

All `G` members are in scope unprefixed in `.csx` scripts. The script holds a `CancellationToken Ct` (fires on cancel/abort/`Finish()`); pass it to all async work. Every `OnIntercept`/`On*`/`Receive` registration is auto-removed when the script ends. Timeouts: `DEFAULT_TIMEOUT = 10000`, `DEFAULT_LONG_TIMEOUT = 30000` ms. Blocking calls run synchronously but honour `Ct`.

### Globals, Connection & Client

| Member | Type / signature | Purpose |
|---|---|---|
| `Client` | `ClientType` | `Flash` / `Unity` / `Shockwave` / `Unknown` — runtime-detected |
| `ClientIdentifier` / `ClientVersion` | `string` | Client build id / version |
| `Hotel` | `Hotel` | Hotel/region enum |
| `Ct` | `CancellationToken` | Cancel/abort/`Finish()` signal — pass to all async ops |
| `Run` | `bool` | `!Ct.IsCancellationRequested` — loop guard |
| `IsFinished` | `bool` | True only after `Finish()` (not external cancel) |
| `In` / `Out` | `Incoming` / `Outgoing` | Header collections — `In.Chat`, `Out.Move`. Never hardcode numbers |
| `Messages` | `IMessageManager` | Raw header manager (prefer `In`/`Out`) |
| `FigureData` / `FurniData` / `ProductData` / `Texts` | game-data objects | Throw if not yet loaded |
| `Global` | `dynamic` | Cross-script shared bag; missing keys return `null` |
| `Delay(int\|TimeSpan)` | `void` | Sync sleep; throws `OperationCanceledException` on cancel |
| `DelayAsync(int\|TimeSpan)` | `Task` | Async delay |
| `Wait()` | `void` | Block forever until cancelled (keeps intercept-driven scripts alive) |
| `Finish()` | `void` | Cancel `Ct` + throw `OperationCanceledException` — stop cleanly from a callback |
| `RunTask(Action)` | `void` | Run on thread pool; body must respect `Run`/`Delay` for exit |
| `Log(string\|object?)` / `Log()` | `void` | Append to output panel |
| `Status(string?\|object?)` | `void` | Update status text in script list |
| `Error(string)` | `ScriptException` | **Returns** an exception — you `throw` it yourself |
| `ToJson(object?, bool indented=true)` / `FromJson<T>(string)` | `string` / `T?` | JSON round-trip |
| `InitGlobal(string, dynamic)` / `InitGlobal(string, Func<dynamic>)` | `bool` | Create shared var only if absent; factory variant avoids building when present |
| `Distance(Point, Point)` | `static double` | Euclidean distance |
| `ShowBubble(string msg, int? index=null, int bubble=30, ChatType type=Whisper)` | `void` | Client-side-only chat bubble (injected as `In.*`; server never sees it). `index` defaults to `Self?.Index ?? -1` |

```csharp
while (Run) {              // canonical loop; Delay throws on cancel -> clean exit
    Status($"tick {DateTime.Now:T}");
    Delay(5000);
}
```

Gotchas:
- `Finish()` re-throws immediately; code after it in the same call stack never runs.
- `Global` is shared across all running scripts — use `InitGlobal` to avoid start-up races.
- `Error("msg")` does not throw; write `throw Error("msg")`.
- Game-data properties throw before load; they auto-load on connect/room entry.

### Room (state & management)

State: `IsInRoom`, `IsLoadingRoom`, `IsRingingDoorbell`, `IsInQueue`, `QueuePosition` (`int`, 0-based), `RoomId` (`long`, `-1` if none), `Room` (`IRoom?`, `null` if none), `DoorTile` (`Tile?`), `Heightmap` (`IHeightmap?`), `FloorPlan` (`IFloorPlan?`).
Permissions: `IsRoomOwner`, `CanMute`, `CanKick`, `CanBan`, `CanUnban` (alias of `IsRoomOwner`).

| Method | Signature → return | Notes |
|---|---|---|
| `EnsureEnterRoom` | `(long roomId, string password="", int timeout=10000) → RoomEntryResult` | **Blocking, confirmed** entry. Manipulates packets in-flight. Result: `Success`/`Full`/`Banned`/`Unknown` (the `InvalidPassword` enum value is defined but not currently produced — a wrong password yields `Unknown`) |
| `EnterRoom` | `(long roomId, string password="") → void` | Fire-and-forget `Out.FlatOpc`; no confirmation |
| `LeaveRoom` | `() → void` | `Out.Quit` |
| `GetRoomData` | `(long roomId, int timeout=10000) → IRoomData` | Blocking public room info |
| `GetRoomSettings` | `(long roomId, int timeout=10000) → RoomSettings` | Editable settings; **owner only** |
| `SaveRoomSettings` | `(RoomSettings) → void` | `Out.SaveRoomSettings` |
| `ModifyRoomSettings` | `(Action<RoomSettings> updater, long? roomId=null, int timeout=10000) → void` | Fetch → mutate → save; defaults to current room |
| `CreateRoom` | `(string name, string description, string model, RoomCategory=PersonalSpace, int maxUsers=50, TradePermissions=NotAllowed) → void` | `Out.CreateNewFlat`, no confirm |
| `DeleteRoom` | `(long roomId) → void` | `Out.DeleteFlat` |
| `GetRights` | `(int timeout=10000) → IReadOnlyList<(long Id, string Name)>` | Current room; throws if not in room |
| `GetRightsFor` | `(long roomId, int timeout=10000) → IReadOnlyList<(long Id, string Name)>` | Any room; times out if not owner |

`IRoom` (via `Room`): `Id`, `Name`, `OwnerName`, `OwnerId`, `Access` (`RoomAccess`), `IsOpen/IsDoorbell/IsLocked/IsInvisible`, `MaxUsers`, `Trading` (`TradePermissions`), `Score`, `Category`, `Tags`, `IsGroupRoom`, `GroupId/GroupName/GroupBadge`, `HasEvent`, `Model`, `Floor/Wallpaper/Landscape`, `DoorTile`, `EntryDirection`, `FloorPlan`, `Heightmap`, `HideWalls`, `WallThickness/FloorThickness`. Collections: `Furni`, `FloorItems`, `WallItems`, `Entities`, `Users`, `Pets`, `Bots`. Lookups: `GetEntity<T>(int|string)`, `GetEntityById<T>(long)`, `TryGetUserByName(name, out user)`, `GetFloorItem(long)`, `GetWallItem(long)`, `GetFurni(ItemType, long)`. Config: `Moderation`, `ChatSettings`.

Enums — `RoomAccess`: `Open=0, Doorbell=1, Password=2, Invisible=3, Friends=7`. `TradePermissions`: `NotAllowed=0, RightsHolders=1, Allowed=2`. `RoomCategory`: `Party=2, Games=3, FansiteSquare=5, HelpCenters=6, PersonalSpace=10, BuildingAndDecoration=11, ChatAndDiscussion=12, Trading=14, Agencies=16, RolePlaying=17`. `RoomEntryResult`: `Unknown, Full, Banned, InvalidPassword, Success`.

```csharp
var result = EnsureEnterRoom(12345678);
if (result != RoomEntryResult.Success) throw Error($"Entry failed: {result}");

ModifyRoomSettings(s => {
    s.Access = RoomAccess.Password;
    s.Password = "secret";
});
```

Gotchas:
- `EnsureEnterRoom` confirms entry (use it when correctness matters); `EnterRoom` is fire-and-forget for already-loaded navigator rooms.
- All room-scoped helpers call a private `RequireRoom()` and throw `InvalidOperationException("The user is not in a room.")` when not in a room.
- `GetRoomSettings`/`GetRights` need ownership; they time out otherwise.

### Movement & Navigation

| Member | Signature | Notes |
|---|---|---|
| `Move` | `(int x, int y)` / `(Point)` / `(IFloorEntity)` | `Out.Move` pathfind request; entity overload picks a random tile in its `Area`. Fire-and-forget — does **not** await arrival |
| `LookTo` | `(int x, int y)` / `(Point)` | `Out.LookTo` — face a tile without moving |
| `Turn` | `(int dir 0-7)` / `(Directions)` | Rotate in place via magic vector |

`Directions`: `North=0, NorthEast=1, East=2, SouthEast=3, South=4, SouthWest=5, West=6, NorthWest=7`.

Navigator (all blocking):

| Member | Signature → return | Notes |
|---|---|---|
| `GetNav` | `(string category, string filter="", int timeout=10000) → NavigatorSearchResults` | Raw results. Has `.GetRooms()` (dedup by id), `.FindRooms(name?, description?, ownerId?, owner?, access?, trading?, category?, groupId?, group?)`, `.FindRoom(name)` |
| `SearchNav` | `(string category, string filter="", int timeout=10000) → IEnumerable<IRoomInfo>` | `GetNav(...).GetRooms()` |
| `QueryNav` | `(string query, int timeout=10000) → IEnumerable<IRoomInfo>` | Category `"query"` — the "Anything" box |
| `SearchNavByName` | `(string roomName, ...) → IEnumerable<IRoomInfo>` | filter `roomname:<name>` |
| `SearchNavByOwner` | `(string ownerName, ...)` | filter `owner:<name>` |
| `SearchNavByTag` | `(string tag, ...)` | filter `tag:<tag>` |
| `SearchNavByGroup` | `(string group, ...)` | filter `group:<group>` |

Common categories: `"query"`, `"hotel_view"`, `"popular"`, `"my_rooms"`, `"my_fav"`, `"my_history"`, `"my_groups"`, `"my_friends_rooms"`, `"official"`.
`IRoomInfo`: `Id, Name, OwnerId, OwnerName, Access, IsOpen/IsDoorbell/IsLocked/IsInvisible, Users, MaxUsers, Description, Trading, Score, Ranking, Category, Tags, IsGroupRoom, HasEvent, GroupId/GroupName/GroupBadge, EventName/EventDescription/EventMinutesRemaining`.

```csharp
var room = SearchNavByOwner("Sulake").FirstOrDefault() ?? throw Error("not found");
EnsureEnterRoom(room.Id);
Move(5, 5);
Delay(2000);
Turn(Directions.North);
```

Gotchas:
- No `WalkTo` that awaits arrival exists. To detect arrival, poll `Self.XY` / a user's position, or subscribe to `OnEntityUpdated`/`OnEntitySlide`.
- `Move(x,y)`/`LookTo`/`Turn` are client-agnostic; the task layer handles Flash/Unity wire differences.

### Entities & Users

Collections (empty when not in room): `Entities` (`IEnumerable<IEntity>`), `Users` (`IRoomUser`), `Pets` (`IPet`), `Bots` (`IBot`), `Self` (`IRoomUser?`, resolved via `GetUserById(UserId)`; `null` until own entity arrives).
Lookups (all `null` if missing): `GetEntityByIndex(int)`, `GetEntity(string)`, `GetEntityById(long)`, and typed `GetUser/GetPet/GetBot` each by `(int index)`, `(string name)`, `…ById(long)`.

| Action | Signature | Sends |
|---|---|---|
| `Respect` | `(long userId)` / `(IRoomUser)` | `Out.RespectUser` |
| `FriendRequest` | `(IRoomUser)` / `(string name)` | `Out.RequestFriend` |
| `Ignore` / `Unignore` | `(IRoomUser)` / `(string name)` | `Out.IgnoreUser` / `Out.UnignoreUser` |
| `Scratch` | `(long petId)` / `(IPet)` | `Out.RespectPet` |
| `Ride` | `(long petId, bool mount)` / `(IPet, bool)` | `Out.MountPet` |
| `Mount` / `Dismount` | `(long)` / `(IPet)` | `Out.MountPet` (shorthands) |

`IEntity` (all): `Id` (persistent), `Index` (ephemeral room slot), `Name`, `Motto`, `Figure`, `Type` (`EntityType`: `User=1, Pet=2, PublicBot=3, PrivateBot=4`), `Location` (`Tile`), `X`/`Y` (`int`), `Z` (`float`), `XY` (`Point`), `Direction` (0-7), `Area`, `Dance` (0=none), `IsIdle`, `IsTyping`, `HandItem` (0=none), `Effect` (0=none), `IsRemoved`, `IsHidden`, `CurrentUpdate`/`PreviousUpdate` (`IEntityStatusUpdate?`).
`IEntityStatusUpdate`: `Index`, `Location`, `HeadDirection`, `Direction`, `Status`, `Stance` (`Stances`: `Stand/Sit/Lay`), `IsController`, `ControlLevel`, `IsTrading`, `MovingTo` (`Tile?`, null if standing), `SittingOnFloor`, `ActionHeight` (`double?`), `Sign` (`Signs`).
`IRoomUser` adds: `Gender`, `GroupId/GroupStatus/GroupName`, `FigureExtra`, `AchievementScore`, `IsModerator`, `RightsLevel`, `HasRights` (`RightsLevel > 0`).
`IPet` adds: `Breed`, `OwnerId/OwnerName`, `RarityLevel`, `HasSaddle`, `IsRiding`, `CanBreed/CanHarvest/CanRevive`, `HasBreedingPermission`, `Level`, `Posture`.
`IBot` adds: `Gender`, `OwnerId/OwnerName`, `Data` (`IReadOnlyList<short>`).

Own profile (`G.User`) — instant cached props (throw if not loaded): `UserData` (`IUserData`), `UserId`, `UserName`, `UserGender`, `UserFigure`, `UserMotto`, `UserNameChangeable`, `UserAchievements`, `UserCredits`, `UserPoints` (`ActivityPoints`), `UserDiamonds` (`UserPoints[Diamond]`), `UserDuckets` (`UserPoints[Ducket]`, silently `10` if missing). `UserData` extras: `RealName`, `DirectMail`, `TotalRespects`, `RespectsLeft`, `ScratchesLeft`, `IsSafetyLocked`, `LastAccessDate`, `StreamPublishingAllowed`. `ActivityPointType`: `Ducket=0, Seashell=1, Heart=2, GiftPoint=3, Shell=4, Diamond=5`.

| Member | Signature → return | Notes |
|---|---|---|
| `SetUserMotto` | `(string) → void` | `Out.ChangeAvatarMotto` |
| `SetUserFigure` | `(string, Gender) → void` / `(string) → void` | Single-arg infers gender via `Figure.Parse`; throws if `Unisex` |
| `GetUserBadges` | `(int timeout=10000) → List<Badge>` | `Badge`: `Id`, `Code` |
| `GetUserGroups` | `(int timeout=10000) → List<GroupInfo>` | `GroupInfo`: `Id, Name, BadgeCode, PrimaryColor, SecondaryColor, IsFavorite, OwnerId, HasForum` |
| `GetUserAchievements` | `(int timeout=10000) → IAchievements` | Network fetch (distinct from cached prop) |
| `GetUserRooms` | `(int timeout=10000) → IEnumerable<IRoomInfo>` | `SearchNav("my","")` |

`Gender`: `Male=0x01, Female=0x02, Unisex=Male|Female` (flags).

```csharp
foreach (var u in Users.Where(u => u.Id != UserId)) { Respect(u); Delay(500); }

Log($"{UserName} ({UserGender}) Credits:{UserCredits} Diamonds:{UserDiamonds}");
var pet = Pets.FirstOrDefault(p => p.OwnerName == UserName && p.HasSaddle);
if (pet != null) Mount(pet);
```

Gotchas:
- `Index` is reused after leave/rejoin — never persist it across room changes; key off `Id`.
- `Self` is `null` until the own entity appears in the room entity list.
- `GetUser(string)` name match is case-sensitive (server-driven). `UserDuckets == 10` is a fallback, not a confirmed balance (Shockwave).

### Furni & Items

Room collections: `Furni` (`IEnumerable<IFurni>`), `FloorItems` (`IFloorItem`), `WallItems` (`IWallItem`), `GetFloorItem(long id)`, `GetWallItem(long id)` (`null` if missing).

| Member | Signature | Notes |
|---|---|---|
| `UseFurni` / `ToggleFurni` | `(IFurni)` / `(IFurni, int state)` | Dispatches by `Type`; Use = state 0 |
| `UseFloorItem` / `ToggleFloorItem` | `(long id)` / `(long id, int state)` | `Out.UseStuff` |
| `UseWallItem` / `ToggleWallItem` | `(long id)` / `(long id, int state)` | `Out.UseWallItem` |
| `UseGate` | `(long id)` / `(IFloorItem)` | `Out.EnterOneWayDoor` |
| `Place` | `(IInventoryItem, Point, int dir=0)` / `(IInventoryItem, WallLocation)` | Validates `Type`; uses `item.ItemId` |
| `PlaceFloorItem` | `(long itemId, Point, int dir=0)` | Protocol-aware (Flash string / Unity ints) |
| `PlaceWallItem` | `(long itemId, WallLocation)` / `(long itemId, string location)` | Protocol-aware |
| `Move` | `(IFloorItem, Point, int dir=0)` / `(IWallItem, WallLocation\|string)` | `Out.MoveRoomItem` / `Out.MoveWallItem` |
| `MoveFloorItem` / `MoveWallItem` | `(long id, Point, int dir=0)` / `(long id, WallLocation\|string)` | Raw by id |
| `Pickup` | `(IFurni)` | Dispatches floor (type 2) / wall (type 1) |
| `PickupFloorItem` / `PickupWallItem` | `(long id)` | `Out.PickItemUpFromRoom` |
| `DeleteWallItem` | `(IWallItem)` / `(long id)` | `Out.RemoveItem` (stickies/photos) |
| `UpdateStackTile` | `(IFloorItem, float height)` / `(long id, float height)` | `Out.StackingHelperSetCaretHeight`; height in tiles (1.0f = one tile) |

`ItemType`: `Floor='s', Wall='i', Badge='b', Effect='e', Bot='r'`.
`IItem`: `Type`, `Kind` (sprite id), `Id`. `IFurni`: `OwnerId`, `OwnerName`, `State` (-1 if non-numeric), `SecondsToExpiration`, `Usage`, `IsHidden`. `IFloorItem`: `X`, `Y`, `XY`, `Z` (`double`), `Height` (`float`), `Extra` (`long`, consumable/teleporter link), `Data` (`IItemData`), `StaticClass`. `IWallItem`: `Location` (`WallLocation`), `WX/WY`, `LX/LY`, `Orientation`, `Data` (raw string). `IItemData`: `Type`, `Flags`, `IsLimitedRare`, `UniqueSerialNumber`, `UniqueSeriesSize`, `Value` (raw state string), `State` (-1 if non-numeric).
`Point`: `X`, `Y`; tuple `(3,5)` and `Tile` implicitly convert; `+`/`-` operators.
`WallLocation`: `WX/WY`, `LX/LY`, `Orientation`; `Parse(string)`/`TryParse`, implicit from string, `Offset(wx,wy,scale)`, `Add(...)`, `Flip()`, `Orient(...)`, `WallLocation.Zero`, `ToString()` → `:w=WX,WY l=LX,LY o`.

```csharp
var item = Inventory.First(i => i.Type == ItemType.Floor && FurniData.GetInfo(i).Identifier == "throne");
Place(item, (5, 7), dir: 2);
Delay(500);
var placed = FloorItems.First(f => f.Kind == item.Kind);
ToggleFloorItem(placed.Id, 1);
```

Gotchas:
- For placement use `item.ItemId` (inventory slot), never `item.Id`. `Place(IInventoryItem,...)` does this for you.
- `PlaceFloorItem`/`PlaceWallItem` branch on `Client`; `Shockwave` throws `"Unknown client protocol."`. Move/pickup are protocol-agnostic.
- `FurniData["identifier"]` indexer throws if missing — use `TryGetInfo` (matching is case-insensitive).

### Inventory

| Member | Signature → return | Notes |
|---|---|---|
| `Inventory` | `IInventory?` | Cached; `null` if never loaded; may be stale if `IsInvalidated` |
| `EnsureInventory` | `(int timeout=30000) → IInventory` | Returns if valid, else requests. **Must be in a room** or server never responds; blocks/throws on timeout |
| `InventoryManager` | `InventoryManager` | Rarely needed |

`IInventory : IEnumerable<IInventoryItem>`: `IsInvalidated`, `GetItem(long id)` (`?`), `TryGetItem(long id, out item)`.
`IInventoryItem : IItem`: `Id`, `ItemId` (slot id — **use for trade/offer/place**), `Type`, `Kind`, `Category` (`FurniCategory`: `Unknown/Normal/Wallpaper/Floor/Landscape/Sticky/Poster/Trax/Disk/Gift/MysteryBox/Trophy/…/Clothing`), `Data`, `IsTradeable`, `IsRecyclable`, `IsGroupable`, `IsSellable`, `SecondsToExpiration`, `HasRentPeriodStarted`, `RoomId` (non-zero if placed), `SlotId`, `Extra`.
Pet inventory: `PetInventory` (`IPetInventory?`), `EnsurePetInventory(int timeout=30000)`. `IPetInventory : IEnumerable<IInventoryPet>` with `IsInvalidated`, `GetItem`/`TryGetItem`. `IInventoryPet`: `Id, Name, TypeId, PaletteId, Color, BreedId, CustomParts (List<int[]>), Level`.
Events (Action-only): `OnInventoryItemAdded/Updated/Removed(Action<InventoryItemEventArgs>)`.

```csharp
var inv = EnsureInventory();
foreach (var x in inv.Where(i => i.IsTradeable && i.Type == ItemType.Floor))
    Log($"{x.Kind} id={x.ItemId}");
```

Gotchas:
- `EnsureInventory` requires being in a room. Re-call when `IsInvalidated`.
- `ItemId` vs `Id`: pass `ItemId` to offer/place — `Id` may silently send the wrong value on Flash.

### Catalog & Marketplace

All catalog/marketplace calls are blocking, default timeout 10000 ms.

| Member | Signature → return | Notes |
|---|---|---|
| `GetCatalog` | `(string type="NORMAL", int timeout=10000) → ICatalog` | Full page tree |
| `GetBcCatalog` | `(int timeout=10000) → ICatalog` | `type="BUILDERS_CLUB"` |
| `GetCatalogPage` | `(int pageId, string type="NORMAL", int timeout=10000) → ICatalogPage` / `(ICatalogPageNode node, int timeout=10000) → ICatalogPage` | Node overload uses `node.Catalog?.Type ?? "NORMAL"` |
| `GetBcCatalogPage` | `(int pageId, int timeout=10000) → ICatalogPage` | BC shorthand |
| `Purchase` | `(ICatalogOffer offer, int count=1, string extra="")` / `(int pageId, int offerId, int count=1, string extra="")` | Requires `offer.Page != null`; fire-and-forget |
| `PurchaseAsGift` | `(ICatalogOffer, string recipient, string message="", string extra="", string? giftFurni=null, GiftBox box=Basic, GiftDecor decor=None)` | Validates offer is Gift-eligible; `giftFurni` defaults to random `present_gen*` |

`extra`: trophy inscription text / group id (as string for group furni) / else `""`.
`ICatalog`: `RootNode`, `Type`, `NewAdditionsAvailable`, `FindNode(title?, name?, id?)`, `FindNode(Func<…,bool>)`, enumerable over nodes.
`ICatalogPageNode`: `Id` (use with `GetCatalogPage`), `Name`, `Title`, `IsVisible`, `Icon`, `OfferIds`, `Children`, `Catalog`, `Parent`, `FindNode(...)`, `EnumerateDescendantsAndSelf()`.
`ICatalogPage`: `Id`, `CatalogType`, `LayoutCode`, `Offers`, `Images`, `Texts`, `AcceptSeasonCurrencyAsCredits`, `Data`.
`ICatalogOffer`: `Id`, `Page` (required for Purchase), `FurniLine`, `IsRentable`, `PriceInCredits`, `PriceInActivityPoints`, `ActivityPointType`, `CanPurchaseAsGift`, `CanPurchaseMultiple`, `Products`, `ClubLevel` (0/1HC/2VIP), `IsPet`, `PreviewImage`.
`ICatalogProduct : IItem`: `Variant`, `Count`, `IsLimited`, `LimitedTotal`, `LimitedRemaining`.
Gift enums — `GiftBox`: `Basic=-1, Royal=0, Imperial=1, Glamor=2, Cardboard=3, Steel=4, IceCube=5, Wooden=6, Valentines=8`. `GiftDecor`: `RedSilkKnotRibbon=0 … None=10`. `GiftFurni` constants: `BasicRed="present_gen" … BasicGray="present_gen6"`, `WrapMaroon="present_wrap*1" … "present_wrap*10"`.

Marketplace (blocking, 10000 ms):

| Member | Signature → return | Notes |
|---|---|---|
| `GetUserMarketplaceOffers` | `(int timeout=10000) → IUserMarketplaceOffers` | Own listings; has `CreditsWaiting` |
| `SearchMarketplace` | `(string? searchText=null, int? from=null, int? to=null, MarketplaceSortOrder sort=HighestPrice, int timeout=10000) → IEnumerable<IMarketplaceOffer>` | `from`/`to` = credit price range |
| `GetMarketplaceInfo` | `(ItemType type, int kind, …)` / `(IItem, …)` / `(FurniInfo, …) → IMarketplaceItemInfo` | Price history |

`MarketplaceSortOrder`: `HighestPrice=1, LowestPrice=2, MostTrades=3, LeastTrades=4, MostOffers=5, LeastOffers=6`.
`IMarketplaceOffer : IItem`: `Id`, `Status` (`Open=1, Sold=2, NotSold=3`), `Data`, `Price`, `TimeRemaining` (min), `Average`, `Offers`.
`IMarketplaceItemInfo : IItem`: `Average` (7-day), `Offers` (open), `TradeInfo` (`IMarketplaceTradeInfo`: `DayOffset`, `AverageSalePrice`, `TradeVolume`).

```csharp
var node = GetCatalog().FindNode(title: "Rare Furniture");
var page = GetCatalogPage(node);
var offer = page.Offers.First(o => o.Products.Any(p => FurniData.GetInfo(p).Identifier == "rare_dragonlamp_sd"));
Purchase(offer);

foreach (var o in SearchMarketplace("dragon lamp", sort: MarketplaceSortOrder.LowestPrice).Take(5))
    Log($"Id={o.Id} {o.Price}c avg={o.Average}c {o.TimeRemaining}m");
```

Gotchas:
- `Purchase` / `PurchaseAsGift` need `offer.Page != null` — get offers via a `GetCatalogPage` result, not detached.
- All are fire-and-forget for the purchase itself (no confirmation wait); poll inventory afterward.

### Trade

State: `IsTrading`, `IsTrader` (you initiated), `HasAcceptedTrade`, `HasPartnerAcceptedTrade`, `IsTradeWaitingConfirmation`, `TradePartner` (`IRoomUser?`), `OwnTradeOffer`/`PartnerTradeOffer` (`ITradeOffer?`).
`ITradeOffer`: `UserId` (`int`), `Items` (`IReadOnlyList<ITradeItem>` — extends `IInventoryItem` + `CreationDay/Month/Year`), `FurniCount`, `CreditCount`.

| Member | Signature | Notes |
|---|---|---|
| `Trade` | `(IRoomUser)` / `(int userIndex)` | Send trade request |
| `Offer` | `(IInventoryItem)` / `(long itemId)` / `(IEnumerable<IInventoryItem>)` / `(IEnumerable<long>)` | Add item(s) (`Out.TradeAddItems`); nulls filtered |
| `CancelOffer` | `(IInventoryItem)` / `(long itemId)` | Remove from your offer |
| `AcceptTrade` | `()` | Stage 1 accept |
| `ConfirmTrade` | `()` | Stage 2 confirm (after both accepted) |
| `CancelTrade` | `()` | Abort (`Out.TradeClose`) |

Events (`Action<T>` + `Func<T,Task>`): `OnTradeOpened(TradeStartEventArgs)`, `OnTradeOpenFailed(TradeStartFailEventArgs)`, `OnTradeUpdated(TradeOfferEventArgs)`, `OnTradeAccepted(TradeAcceptEventArgs)`, `OnTradeWaitingConfirm(EventArgs)`, `OnTradeClosed(TradeStopEventArgs)`, `OnTradeCompleted(TradeCompleteEventArgs)`.

```csharp
var target = Users.First(u => u.Name == "TargetUser");
Trade(target);
await DelayAsync(500);
Offer(EnsureInventory().Where(x => x.IsTradeable));
await DelayAsync(1000);
AcceptTrade();
while (!IsTradeWaitingConfirmation) await DelayAsync(200);
ConfirmTrade();
```

Gotchas:
- Two-stage flow: `AcceptTrade()` → wait for `IsTradeWaitingConfirmation` → `ConfirmTrade()`. `ConfirmTrade()` before both accept is a no-op.
- Any offer change after acceptance resets accepted state — re-accept.

### Chat & Texts

| Member | Signature | Notes |
|---|---|---|
| `Talk` | `(string message, int bubble=0)` | `Out.Chat`; normal speech (sends trailing `-1` internally) |
| `Shout` | `(string message, int bubble=0)` | `Out.Shout` |
| `Whisper` | `(IRoomUser recipient, string message, int bubble=0)` / `(string recipient, …)` | Prepends recipient name automatically |
| `Chat` | `(ChatType chatType, string message, int bubble=0)` | Low-level dispatcher |

`ChatType`: `Talk=0, Shout=1, Whisper=2`. Bubble `0` = hotel default.

External texts (`Texts` dict; `Get*` returns `string?`/`null`, `TryGet*` returns `bool` + out):

| Member | Key |
|---|---|
| `GetBadgeName(code)` / `TryGetBadgeName(code, out name)` | `badge_name_{code}` |
| `GetBadgeDescription(code)` / `TryGetBadgeDescription` | `badge_desc_{code}` |
| `GetEffectName(id)` / `TryGetEffectName` | `fx_{id}` |
| `GetEffectDescription(id)` / `TryGetEffectDescription` | `fx_{id}_desc` |
| `GetHandItemName(id)` / `TryGetHandItemName` | `handitem{id}` |
| `GetHandItemIds(name)` | reverse scan → `IEnumerable<int>` |

```csharp
Talk("hello room");
Shout("HELLO EVERYONE", 2);
var u = Users.First(u => u.Name == "Ducks");
Whisper(u, "hey");
string? name = GetBadgeName("ADI");
```

Gotchas:
- Never prepend the recipient name to `Whisper` yourself — both overloads do it.
- Use `GetBadgeName` over `TryGetBadgeName` (the `TryGet` variant queries the raw code without the `badge_name_` prefix — inconsistent).
- `GetHandItemIds` full-scans `Texts`; don't call it in tight loops.

### Effects & Actions

Effects (`G.Effects`, fire-and-forget):

| Member | Sends | Notes |
|---|---|---|
| `ActivateEffect(int effectId)` | `Out.ActivateAvatarEffect` | **Inventory action — consumes a non-permanent effect.** Use with care |
| `EnableEffect(int effectId)` | `Out.UseAvatarEffect` | Equip/display an already-owned effect |
| `DisableEffect()` | `Out.UseAvatarEffect` (-1) | Remove current effect |

Actions (`G.Actions`, fire-and-forget):

| Member | Notes |
|---|---|
| `Action(int)` / `Action(Actions)` | Raw/typed expression (`Out.Expression`) |
| `Wave()` / `ThumbsUp()` / `Idle()` / `Unidle()` | `Actions.Wave/ThumbsUp/Idle/None` |
| `Sit()` / `Sit(bool)` / `Stand()` | `Out.Posture` (1=sit, 0=stand) |
| `Dance()` / `Dance(int)` / `Dance(Dances)` / `StopDancing()` | `Out.Dance`; `Dance()`=id 1, 0=stop |
| `Sign(int)` / `Sign(Signs)` | `Out.ShowSign` |

`Actions`: `None=0, Wave=1, Kiss=2, Laugh=3, Idle=5, Jump=6, ThumbsUp=7`. `Dances`: `None=0, Dance=1, PogoMogo=2, DuckFunk=3, TheRollie=4`. `Signs`: `None=-1, Zero=0 … Ten=10, Heart=11, Skull=12, Exclamation=13, SoccerBall=14, Smile=15, RedCard=16, YellowCard=17`.

```csharp
Wave();
await DelayAsync(2000);
Dance(Dances.PogoMogo);
await DelayAsync(3000);
StopDancing();

EnableEffect(97);
await DelayAsync(5000);
DisableEffect();
```

Gotchas:
- `ActivateEffect` (inventory) ≠ `EnableEffect` (display). `ActivateEffect` decrements quantity on consumable effects — prefer `EnableEffect` to just switch the visible effect.
- `Kiss`/`Laugh`/`Jump` have no shortcut — use `Action(Actions.Kiss)` etc.
- `Sit`/`Stand` use `Out.Posture`, `Sign` uses `Out.ShowSign` — distinct from expressions.

### Friends, Groups & Moderation

Friends — `Friends` (`IEnumerable<IFriend>`). `IFriend`: `Id, Name, Gender, IsOnline, CanFollow, FigureString, CategoryId, Motto, RealName, IsAcceptingOfflineMessages, IsVipMember, IsPocketHabboUser, Relation` (`None/Heart/Smile/Bob`).

| Member | Signature | Notes |
|---|---|---|
| `IsFriend` | `(long id)` / `(string name)` / `(IRoomUser)` | Membership check |
| `AddFriend` | `(string name)` / `(IRoomUser)` | Send request |
| `RemoveFriend` / `RemoveFriends` | `(long)` / `(IFriend)` / `(IEnumerable<long>)` / `(IEnumerable<IFriend>)` | Remove |
| `AcceptFriendRequest` / `AcceptFriendRequests` | `(long)` / `(IEnumerable<long>)` | Accept |
| `DeclineFriendRequest` / `DeclineFriendRequests` / `DeclineAllFriendRequests` | `(long)` / `(IEnumerable<long>)` / `()` | Decline |
| `SendMessage` | `(long userId, string message)` / `(IFriend, string message)` | Private message |

Groups:

| Member | Signature → return | Notes |
|---|---|---|
| `JoinGroup` / `LeaveGroup` | `(long groupId)` | Leave kicks self |
| `SetGroupFavourite` / `RemoveGroupFavourite` | `(long groupId)` | Favourite badge |
| `GetGroup` | `(long groupId, int timeout=10000) → IGroupData` | Blocking |
| `GetGroupMembers` | `(long groupId, int page=0, string filter="", GroupMemberSearchType searchType=Members, int timeout=10000) → IGroupMembers` | Page is **0-based** |
| `AcceptGroupMember` / `RejectGroupMember` / `KickGroupMember` | `(long groupId, long userId)` | Manage members |

`GroupMemberSearchType`: `Members=0, Admins=1, Requests=2`.
`IGroupData`: `Id, Name, Description, Badge, HomeRoomId/HomeRoomName, Type, IsGuild, MemberStatus, MemberCount, PendingRequests, IsFavourite, IsOwner, IsAdmin, OwnerName, CanDecorateHomeRoom, HasForum, Created`.
`IGroupMembers : IReadOnlyList<IGroupMember>`: `GroupId, GroupName, HomeRoomId, BadgeCode, TotalEntries, PageIndex, PageSize, IsAllowedToManage, SearchType, Filter`. `IGroupMember`: `Id, Name, Figure, Type, Joined (DateTime)`.

Moderation (need rights/admin; `roomId` defaults to current via `RequireRoom()`):

| Member | Signature | Notes |
|---|---|---|
| `Mute` | `(long userId, int minutes, long? roomId=null)` / `(IRoomUser, int minutes)` | Timed mute |
| `Kick` | `(long userId)` / `(IRoomUser)` | Kick |
| `Ban` | `(long userId, BanDuration)` / `(IRoomUser, BanDuration)` | Current room |
| `Unban` | `(long userId, long? roomId=null)` / `(IRoomUser)` | Unban |
| `GiveRights` | `(long userId)` / `(IRoomUser)` | Grant rights |
| `RemoveRights` | `(IEnumerable<long>)` / `(IEnumerable<IRoomUser>)` | Remove rights |

`BanDuration`: `Hour, Day, Permanent`.

```csharp
long gid = 12345L;
var first = GetGroupMembers(gid, 0);
int pages = (int)Math.Ceiling((double)first.TotalEntries / first.PageSize);
var all = first.ToList();
for (int p = 1; p < pages; p++) all.AddRange(GetGroupMembers(gid, p));
Log($"Loaded {all.Count} members");
```

Gotchas:
- `GetGroupMembers` returns one page; loop `p` until `(p * PageSize) >= TotalEntries`.
- Moderation throws `InvalidOperationException` when not in a room (default `roomId`).

### Stickies & Misc

Stickies:

| Member | Signature → return | Notes |
|---|---|---|
| `PlaceSticky` | `(IInventoryItem, WallLocation)` | Throws if `Category != FurniCategory.Sticky` |
| `PlaceSticky` | `(long itemId, WallLocation)` | No category check |
| `PlaceStickyWithPole` | `(IInventoryItem\|long, WallLocation, string color, string text)` | `Out.AddSpamWallPostIt` |
| `GetSticky` | `(IWallItem\|long, int timeout=10000) → Sticky` | Blocking fetch |
| `UpdateSticky` | `(Sticky)` / `(IWallItem, string color, string text)` / `(long itemId, string color, string text)` | Save changes |
| `DeleteSticky` | `(Sticky)` | Delegates to `DeleteWallItem(sticky.Id)` |

`Sticky`: `Id` (wall item id), `Color` (6-hex), `Text`, `Colors` (static). `StickyColors` (implicit `string`): `Blue="9CCEFF", Pink="FF9CFF", Green="9CFF9C", Yellow="FFFF33"`.

Misc lookups (blocking):

| Member | Signature → return | Notes |
|---|---|---|
| `SearchUser` | `(string name, int timeout=10000) → UserSearchResult?` | Exact (case-insensitive) match or `null` |
| `SearchUsers` | `(string name, int timeout=10000) → UserSearchResults` | `.Friends` + `.Others`; `.GetResult(name)` |
| `GetProfile` | `(long userId, int timeout=10000) → IUserProfile` | Numeric id required; throws on timeout |

`UserSearchResult`: `Id, Name, Motto, Figure, RealName, Online`. `IUserProfile`: `Id, Name, Figure, Motto, Created, ActivityPoints, Friends, IsFriend, IsFriendRequestSent, IsOnline, Groups, LastLogin (TimeSpan), Level, StarGems`.

Randomness (`static`, uses `Random.Shared`): `Rand()`, `Rand(int max)` (max exclusive), `Rand(int min, int max)`, `Rand(byte[])`, `RandDouble()`, `Rand<T>(IEnumerable<T>)` (→ `default` if empty), `Rand<T>(T[])` (throws if empty).

UI (scripter window only): `SetDarkTheme(bool)`, `SetBackgroundColor(byte r, byte g, byte b)`.

```csharp
foreach (var w in WallItems.Where(x => x.Category == FurniCategory.Sticky)) {
    var s = GetSticky(w);
    UpdateSticky(s.Id, StickyColors.Yellow, s.Text);
    Delay(300);
}

var r = SearchUser("Ducks");
if (r != null) {
    var p = GetProfile(r.Id);
    Talk($"{p.Name} joined {p.Created}, friends {p.Friends}");
}
```

Gotchas:
- `SearchUser` resolves name → id; combine with `GetProfile(id)` since `GetProfile` needs the numeric id.
- `Rand<T>(IEnumerable<T>)` returns `default` (possibly `null`) on empty; the array overload throws — guard before use.
- `Rand`/`Rand(min,max)` upper bound is exclusive.

Critical findings now fully grounded. Two important nuances for the `Send<T...>` shorthand and `Read<T>/Write<T>` dispatch:

- `WriteObject`/`Write<T>` dispatch: `long`/`ulong` → `WriteLegacyLong` (NOT `WriteLong`), `float` → `WriteFloat` (raw, NOT legacy), `int` → `WriteInt`. So `Send(Out.Chat, "x", 0, 0)` writes string+int+int. A C# `long` literal becomes a LegacyLong-encoded field; a `float` becomes a raw 4-byte float (which is wrong on Flash — must pass `LegacyFloat`).
- `Read<T>()`: `long` → `ReadLegacyLong`, but `float` → `ReadFloat` (raw, NOT legacy). And `int`/`short` are raw, not legacy. So `Read<int>()` ≠ `ReadLegacyShort()`.

These are exactly the "bites" the spec wants. I now have everything correct. Writing the section.

## Packets, headers & interception

### Header sets: `In`, `Out`, `Client`

| `G` member | Type | Meaning |
|---|---|---|
| `In` | `Incoming` | Server→client headers (`Destination.Client`). `In.Chat`, `In.Credits`, … |
| `Out` | `Outgoing` | Client→server headers (`Destination.Server`). `Out.Chat`, `Out.Move`, … |
| `Client` | `ClientType` | `Flash` or `Unity` (the live session). `Shockwave` exists in the enum but the header system only resolves Flash/Unity slots. |

- `In`/`Out` are `Headers : Dictionary`-style bags. Every known message is a typed `Header` property **and** reachable by indexer: `Out.Chat` ≡ `Out["Chat"]` (case-insensitive). A miss on the indexer throws `UnknownHeaderException`; the typed property won't compile if it doesn't exist.
- A `Header` carries a `Flash` and a `Unity` `ClientHeader?` slot plus an optional raw `Value`. The framework resolves the right numeric wire value per `Client` internally — **never hardcode numbers**.
- **Flash/Unity name divergence:** the typed property name is always the *Unity* name. A message can have a different Flash name pointing at the same `Header` (both names land in the name map). Flash-only messages have a name-map entry but **no typed property** — reach them via `In["FlashOnlyName"]` / `Out["FlashOnlyName"]`. Guard with `In.MessageExists("Name")` / `In.TryGetHeader("Name", out var h)`.
- **Unresolved headers throw:** before the connection is live (no `Load`), or when a name isn't bound for the current client, the internal `GetValue(Client)` throws `UnresolvedHeaderException`. `Send`/`Receive` by name only work on a live session.

### Raw / unmapped / negative headers

When the message manager has no name for what you need (debugging, Shockwave, custom servers), build a `Header` from a raw numeric value. **Naming is from the script author's perspective:**

```csharp
Header inHdr  = Header.In(4001);   // Destination.Client  (incoming, server->client)
Header outHdr = Header.Out(2401);  // Destination.Server  (outgoing, client->server)

Send(outHdr);                       // empty-body raw send
Send(Header.Out(2401), "hello", 0); // raw header + payload (see Send<T...> below)
var pkt = Receive(Header.In(4001), timeout: 5000);
```

- Raw headers bypass the name map entirely — only use as a last resort.
- "Negative headers" surface as `Value <= 0` meaning *unresolved* — `GetValue` throws on `<= 0`. A raw `Header.Out(-1)` is not sendable. If you must encode a `-1` field (e.g. "no target"), that's a **packet field value**, not a header — write it with `WriteInt(-1)` / `WriteLegacyShort(-1)`.

### Sending

| Signature | Notes |
|---|---|
| `void Send(IReadOnlyPacket packet)` | Direction taken from `packet.Header.Destination`. |
| `void Send(Header header)` | Zero-payload packet. |
| `void Send<T1..Tn>(Header header, T1 a1, …)` | **Source-generated shorthand** — builds the packet and type-dispatches each arg (see encoding table). |
| `ValueTask SendAsync(IReadOnlyPacket)` / `SendAsync(Header)` / `SendAsync<T...>(Header, …)` | Async forms. |

```csharp
Send(Out.GetCredits);                       // header only
Send(Out.Chat, "hello room", 0, 0);         // shorthand: string + int + int
await SendAsync(Out.GetCredits);

var p = new Packet(Out.Chat, Client);       // manual build (fluent, returns IPacket)
p.WriteString("hello").WriteInt(0).WriteShort(0);
Send(p);
```

**`Send<T...>` / `Write<T>` / `WriteObject` arg type-dispatch** (this is where Flash bites you):

| C# arg type | Method called | Wire (Flash) | Wire (Unity) |
|---|---|---|---|
| `bool` | `WriteBool` | 1 byte | 1 byte |
| `byte` | `WriteByte` | 1 byte | 1 byte |
| `short` / `ushort` | `WriteShort` | 2 BE | 2 BE |
| `int` / `uint` | `WriteInt` | 4 BE | 4 BE |
| `long` / `ulong` | `WriteLegacyLong` | **4 bytes (truncated)** | 8 bytes |
| `float` | `WriteFloat` | **4-byte float (wrong on Flash!)** | 4-byte float |
| `string` | `WriteString` | u16-len + UTF-8 | same |
| `LegacyShort` | `WriteLegacyShort` | 4 (`int`) | 2 (`short`) |
| `LegacyFloat` | `WriteLegacyFloat` | float-as-string | 4-byte float |
| `LegacyLong` | `WriteLegacyLong` | 4 (`int`) | 8 (`long`) |
| `IComposable` | `Write(IComposable)` | composes self | — |
| `ICollection`/`IEnumerable` | count via `WriteLegacyShort` then each item | int count | short count |

> **Trap:** passing a C# `float` literal sends a raw 4-byte float, which Flash does **not** expect (Flash floats are length-prefixed ASCII). For any float in a cross-client packet, pass a `LegacyFloat`, not a `float`. A C# `long` becomes a *legacy* long (truncates on Flash); pass `int` if the value really is 32-bit.

**`IPacket` write methods** (all fluent → `IPacket`; each has a `(value, int position)` overload that writes at an absolute offset without advancing):

`WriteBool`, `WriteByte`, `WriteShort`, `WriteInt`, `WriteFloat`, `WriteLong`*, `WriteString`, `WriteLegacyShort`, `WriteLegacyFloat`, `WriteLegacyLong`, `WriteFloatAsString`, `WriteBytes(ReadOnlySpan<byte>)`, `Write(IComposable)`, `Replace(params object[])`, `ReplaceString(string)`, `ModifyString(Func<string,string>)`.
\*`WriteLong` **throws** when `Protocol == Flash` — use `WriteLegacyLong`.

### Receiving / one-shot capture

| Signature | Notes |
|---|---|
| `IReadOnlyPacket Receive(HeaderSet, int timeout=-1, bool block=false)` | Sync, blocks. |
| `IReadOnlyPacket Receive(ITuple, int timeout=-1, bool block=false)` | Tuple form: `Receive((In.Chat, In.Shout))`. |
| `Task<IPacket> ReceiveAsync(HeaderSet/ITuple, int timeout=-1, bool block=false)` | Async. |
| `bool TryReceive(HeaderSet, out IReadOnlyPacket? packet, int timeout=-1, bool block=false)` | See timeout caveat below. |

- `timeout = -1` → **wait forever**. `block = true` → captured packet is dropped (never reaches its destination). The returned packet is a **copy** (`e.Packet.Copy()`), safe to read after the call.
- **Timeout throws `TimeoutException`** (the internal cancel is rethrown as `TimeoutException` by the interceptor task), *not* `OperationCanceledException`. Script cancel (`Ct`) propagates as `OperationCanceledException`.
- **`TryReceive` caveat:** its `catch` only handles `OperationCanceledException when (!Ct.IsCancellationRequested)`. A plain timeout surfaces as `TimeoutException`, which `TryReceive` does **not** swallow — it propagates. To treat timeout as "no packet", wrap `Receive` yourself:

```csharp
IReadOnlyPacket? chat = null;
try { chat = Receive((In.Chat, In.Shout), timeout: 5000); }
catch (TimeoutException) { Log("no chat in 5s"); }

Send(Out.GetCredits);                        // request/response idiom
int credits = Receive(In.Credits, timeout: 5000).ReadInt();

var room = await ReceiveAsync(In.RoomReady, timeout: 3000, block: true); // capture + suppress
```

**`IReadOnlyPacket` read methods** (each advances `Position`; most have a `(int pos)` overload that reads at an absolute offset):

| Method | Wire (Flash) | Wire (Unity) |
|---|---|---|
| `ReadBool()` | 1 byte (throws if not 0/1) | same |
| `ReadByte()` | 1 byte | same |
| `ReadShort()` | 2 BE | same |
| `ReadInt()` | 4 BE | same |
| `ReadFloat()` | 4-byte float (Unity only in practice) | 4-byte float |
| `ReadLong()` | **throws on Flash** | 8 BE |
| `ReadString()` | u16-len + UTF-8 | same |
| `ReadFloatAsString()` | parse float from string | same |
| `ReadLegacyShort()` | reads `int` (4) | reads `short` (2) |
| `ReadLegacyFloat()` | string → float | 4-byte float |
| `ReadLegacyLong()` | reads `int` (4) → long | reads `long` (8) |

Navigation: `Position` (get/set; throws if `<0` or `>Length`), `Available` (`Length-Position`), `Length`, `Skip(int bytes)`, `Skip(params Type[])` (uses `Packet.Bool/Byte/Short/Int/Float/Long/String`; protocol-aware for `long`/`float`), `CanReadBool()`, `CanReadString()`.

**Generic read extensions** — note these are **not** all legacy-aware:

| Call | Dispatches to |
|---|---|
| `Read<bool/byte/short/int/string>()` | raw `ReadBool/Byte/Short/Int/String` |
| `Read<long>()` | **`ReadLegacyLong`** (legacy) |
| `Read<float>()` | **`ReadFloat`** (raw — *not* legacy!) |
| `Read<LegacyShort/LegacyFloat/LegacyLong>()` | the matching legacy method |
| `ReadList<T>()` | count via `ReadLegacyShort`, then N × `Read<T>()` |

> So `pkt.Read<int>()` reads a raw 4-byte int on **both** clients (it is *not* a legacy short). For a count/index field that's 4 bytes on Flash but 2 on Unity, use `ReadLegacyShort()` or `Read<LegacyShort>()`. For a float in a cross-client packet, use `ReadLegacyFloat()` / `Read<LegacyFloat>()`, never `Read<float>()`.

### Intercepting (persistent, live callbacks)

| Signature | Notes |
|---|---|
| `void OnIntercept(Header, Action<InterceptArgs>)` | Single header. |
| `void OnIntercept(ITuple, Action<InterceptArgs>)` | `OnIntercept((In.Chat, In.Shout, In.Whisper), e => …)` |
| `void OnIntercept(HeaderSet, Action<InterceptArgs>)` | Set form. |
| `void OnIntercept(Header/ITuple/HeaderSet, Func<InterceptArgs, Task>)` | Async callback — **fire-and-forget** (`callback(e)` is not awaited). |

- Use `OnIntercept` for callbacks that live the whole script; use `Receive*` for one-shot. All registrations are tracked and **auto-removed when the script ends** — no manual cleanup.
- **Async-callback trap:** the `Func<…,Task>` overload wraps to `e => { callback(e); }` — the task is never awaited, so exceptions inside (including bad packet reads) are **silently swallowed**, and blocking decisions made *after* an `await` are too late. Keep `e.Block()` and packet reads synchronous; only `await` after you've already decided.
- The dispatcher sets `e.Packet.Position = 0` before each callback — you always read from the start.
- Registering the *same delegate instance* against the same header twice throws `InvalidOperationException`; distinct lambdas are fine.

**`InterceptArgs`** (do **not** retain past the callback — it's disposed by the framework afterward):

| Member | Type | Meaning |
|---|---|---|
| `Packet` | `IPacket` | Live, readable **and** writable; assign a new `IPacket` to replace it wholesale. |
| `OriginalPacket` | `IReadOnlyPacket` | Frozen snapshot before any edit. |
| `Destination` | `Destination` | `Client` (incoming) / `Server` (outgoing). |
| `IsIncoming` / `IsOutgoing` | `bool` | — |
| `Step` | `int` | Sequence number of the packet stream. |
| `Timestamp` | `DateTime` | Intercept time. |
| `IsBlocked` / `IsModified` | `bool` | Blocked state; whether `Packet` differs from `OriginalPacket`. |
| `Block()` | `void` | Drop the packet. Idempotent, cannot be undone. |

```csharp
// log + conditionally block outgoing chat
OnIntercept(Out.Chat, e => {
    string msg = e.Packet.ReadString();
    if (msg.Contains("spam")) { e.Block(); Log($"blocked: {msg}"); }
});

// rewrite a string field in place (cheapest edit)
OnIntercept(Out.Chat, e => {
    e.Packet.Position = 0;
    e.Packet.ModifyString(s => s.Replace("badword", "***"));
});

// replace the whole packet
OnIntercept(Out.Chat, e => {
    string original = e.Packet.ReadString();
    var modified = new Packet(e.Packet.Header, Client);
    modified.WriteString(original.ToUpper()).WriteInt(0).WriteInt(0);
    e.Packet = modified;       // do NOT dispose e.Packet yourself
});

Wait();   // keep the script alive so callbacks keep firing
```

### Client differences that bite

| Topic | Flash | Unity |
|---|---|---|
| `ReadLegacyShort` / `WriteLegacyShort` | `int` (4 bytes) | `short` (2 bytes) |
| `ReadLegacyLong` / `WriteLegacyLong` | `int` (4 bytes; **write truncates**) | `long` (8 bytes) |
| `ReadLegacyFloat` / `WriteLegacyFloat` | float as ASCII string field | 4-byte float |
| `ReadLong` / `WriteLong` (raw) | **throws** | OK |
| raw `ReadFloat` / `WriteFloat` | 4-byte float = **wrong wire shape** | OK |
| list / collection count prefix | `int` (via LegacyShort) | `short` |
| header numeric resolution | Flash slot | Unity slot |
| typed property name | resolves via name map (Flash name may differ) | property == Unity name |

Rules of thumb:
- For any field whose size differs by client (counts, item/user IDs, floats), use the **Legacy** read/write (or `LegacyShort`/`LegacyFloat`/`LegacyLong` wrapper types with `Read<T>`/`Send<T...>`). Plain `int`/`long`/`float` are raw fixed-width and silently wrong cross-client.
- `Send`/`Receive`/`OnIntercept` by `In.*`/`Out.*` are client-agnostic — the framework resolves the correct numeric header. Only drop to `Header.In(n)`/`Header.Out(n)` when no name exists.
- Branch on `Client == ClientType.Flash` / `ClientType.Unity` only when raw-field layout genuinely diverges and the Legacy helpers don't cover it.

## Events

`On*` methods register **persistent** callbacks for the lifetime of the script. Every `On*` has two overloads — `Action<TEventArgs>` (sync) and `Func<TEventArgs, Task>` (async) — except the three inventory events, which are `Action<T>`-only. All subscriptions are auto-removed when the script ends (`Unsubscriber` cleanup), so you never manually unsubscribe in normal scripts.

For **one-shot** captures use `Receive`/`ReceiveAsync` instead; reserve `On*` for whole-script-lifetime handlers.

### Available events

**Room**

| Method | EventArgs | Fires when |
|---|---|---|
| `OnEnteredQueue` | `EventArgs` | Joined room entry queue |
| `OnQueueUpdate` | `EventArgs` | Queue position changed |
| `OnEnteringRoom` | `EventArgs` | Began entering room |
| `OnEnteredRoom` | `RoomEventArgs` | Fully entered room |
| `OnLeftRoom` | `EventArgs` | Left room |
| `OnKicked` | `EventArgs` | Kicked from room |
| `OnRoomDataUpdate` | `RoomDataEventArgs` | Room metadata updated |

**Floor items**

| Method | EventArgs | Fires when |
|---|---|---|
| `OnFloorItemsLoaded` | `FloorItemsEventArgs` | Initial floor-item load on entry |
| `OnFloorItemAdded` | `FloorItemEventArgs` | Floor item placed |
| `OnFloorItemUpdated` | `FloorItemUpdatedEventArgs` | Floor item moved/rotated |
| `OnFloorItemDataUpdated` | `FloorItemDataUpdatedEventArgs` | State change (gate open, animation, etc.) |
| `OnFloorItemSlide` | `FloorItemSlideEventArgs` | Slid via roller/wired |
| `OnFloorItemRemoved` | `FloorItemEventArgs` | Floor item removed |

**Wall items**

| Method | EventArgs | Fires when |
|---|---|---|
| `OnWallItemsLoaded` | `WallItemsEventArgs` | Initial wall-item load |
| `OnWallItemAdded` | `WallItemEventArgs` | Wall item placed |
| `OnWallItemUpdated` | `WallItemUpdatedEventArgs` | Wall item updated |
| `OnWallItemRemoved` | `WallItemEventArgs` | Wall item removed |

**Inventory** (`Action<T>` only — no async overload)

| Method | EventArgs | Fires when |
|---|---|---|
| `OnInventoryItemAdded` | `InventoryItemEventArgs` | Item added to inventory |
| `OnInventoryItemUpdated` | `InventoryItemEventArgs` | Inventory item updated |
| `OnInventoryItemRemoved` | `InventoryItemEventArgs` | Item removed from inventory |

**Entities**

| Method | EventArgs | Fires when |
|---|---|---|
| `OnEntityAdded` | `EntityEventArgs` | One entity entered room |
| `OnEntitiesAdded` | `EntitiesEventArgs` | Batch of entities loaded |
| `OnEntityUpdated` | `EntityEventArgs` | Entity position/state update |
| `OnEntitySlide` | `EntitySlideEventArgs` | Entity slid on roller |
| `OnUserDataUpdated` | `EntityDataUpdatedEventArgs` | Figure/gender/motto/achievement-score changed |
| `OnEntityIdle` | `EntityIdleEventArgs` | Idle status toggled |
| `OnEntityDance` | `EntityDanceEventArgs` | Dance changed |
| `OnEntityHandItem` | `EntityHandItemEventArgs` | Hand item changed |
| `OnEntityEffect` | `EntityEffectEventArgs` | Effect changed |
| `OnEntityAction` | `EntityActionEventArgs` | Entity performed an action |
| `OnEntityRemoved` | `EntityEventArgs` | Entity left room |

**Chat**

| Method | EventArgs | Fires when |
|---|---|---|
| `OnChat` | `EntityChatEventArgs` | Any entity chats (normal/shout/whisper) |

### Bot loop shape

The idiomatic event-driven script: register all `On*` handlers up front, then call `Wait()` (blocks until cancel/`Finish()`) to keep the script alive so callbacks keep firing. Use `Finish()` inside any callback to stop the loop. Do **not** spin a manual `while (Run)` loop just to keep events alive — `Wait()` is the keep-alive.

```csharp
OnEnteredRoom(e => Log($"Entered: {e.Room.Data?.Name}"));

OnEntityAdded(e => {
    if (e.Entity is IRoomUser u && u.Id != UserId)
        Log($"{u.Name} entered @ ({u.X},{u.Y})");
});

OnChat(e => {
    Log($"{e.Entity.Name}: {e.Message}");
    if (e.Message == "!stop") {
        Log("stopping");
        Finish();
    }
});

OnEntityRemoved(e => Log($"{e.Entity.Name} left"));

Wait();
```

Async handlers work identically — pass a `Func<TEventArgs, Task>`:

```csharp
OnEnteredRoom(async e => {
    Send(Out.GetRoomData);
    var data = await ReceiveAsync(In.RoomData, timeout: 3000);
    Log($"Floor: {data.ReadString()}");
});
Wait();
```

I'll ground this in the provided source digests. Let me write the section directly.

## Data models reference

Objects below are returned live by `G` (the globals class). All are interfaces — concrete classes (`FloorItem`, `RoomUser`, etc.) implement them but you only see the interface. Live state mutates as packets arrive; never cache `Index` across rooms.

### IRoom + RoomData/Info

`G.Room` is `IRoom?` — **null** while loading; gate on `G.IsInRoom` first.

| Member | Type | Notes |
|---|---|---|
| `Id` | `long` | Room ID |
| `Data` | `IRoomData` | Full entry-packet payload |
| `Name` / `Description` | `string` | Shortcuts to `Data` |
| `OwnerId` / `OwnerName` | `long` / `string` | |
| `Access` | `RoomAccess` | `Open=0 Doorbell=1 Password=2 Invisible=3 Friends=7` |
| `IsOpen`/`IsDoorbell`/`IsLocked`/`IsInvisible` | `bool` | derived from `Access` |
| `MaxUsers` | `int` | |
| `Trading` | `TradePermissions` | `NotAllowed=0 RightsHolders=1 Allowed=2` |
| `Score` / `Ranking` | `int` | |
| `Category` | `RoomCategory` | `Party=2 Games=3 PersonalSpace=10 BuildingAndDecoration=11 ChatAndDiscussion=12 Trading=14 RolePlaying=17` (+others) |
| `Tags` | `IReadOnlyList<string>` | |
| `Flags` | `RoomFlags` | `[Flags] HasOfficialRoomPic=1 IsGroupHomeRoom=2 HasEvent=4 ShowOwnerName=8 AllowPets=16 ShowRoomAd=32` |
| `HasEvent`/`IsGroupRoom`/`AllowPets` | `bool` | derived from `Flags` |
| `GroupId`/`GroupName`/`GroupBadge` | `long`/`string`/`string` | valid only when `IsGroupRoom` |
| `EventName`/`EventDescription`/`EventMinutesRemaining` | `string`/`string`/`int` | valid only when `HasEvent` |
| `Model` | `string` | Server room-model id |
| `Floor`/`Wallpaper`/`Landscape` | `string?` | null until loaded |
| `DoorTile` | `Tile` | spawn tile |
| `EntryDirection` | `int` | spawn facing 0–7 |
| `FloorPlan` | `IFloorPlan` | static tile model |
| `Heightmap` | `IHeightmap` | live stacking heights |
| `HideWalls` | `bool` | |
| `WallThickness`/`FloorThickness` | `Thickness` | `Thinnest=-2 Thin=-1 Normal=0 Thick=1` |
| `Moderation` | `IModerationSettings` | mute/kick/ban thresholds |
| `ChatSettings` | `IChatSettings` | |

**Collections** (all lazy `IEnumerable`): `Furni` (floor+wall), `FloorItems`, `WallItems`, `Entities`, `Users`, `Pets`, `Bots`.
**Lookups:** `GetFloorItem(long)`/`GetWallItem(long)`/`GetFurni(ItemType,long)` → `…?`; `HasFloorItem(long)`/`HasWallItem(long)` → `bool`; `GetEntity<T>(int index|string name)`, `GetEntityById<T>(long)`, and `TryGet…` non-throwing variants.

**`IRoomData : IRoomInfo`** adds: `IsEntering` (`bool`, true only on first load of this entry), `Forward`, `IsGroupMember`, `IsRoomMuted`, `Moderation`, `CanMute`, `ChatSettings`. Fetch a room's data without entering via `GetRoomData(roomId)`.

**`RoomInfo`** (navigator entry, from `SearchNav`/`GetNav`) carries the same identity/access/group/event fields plus `Users`/`MaxUsers` counts and `OfficialRoomPicRef`.

### IEntity / IRoomUser (+ status)

`IEntity` is the base for every live entity (users, pets, bots).

| Member | Type | Notes |
|---|---|---|
| `Index` | `int` | **ephemeral** per-room slot; used by status updates/packets — never persist |
| `Id` | `long` | persistent account/entity ID |
| `Name` / `Motto` / `Figure` | `string` | |
| `Location` | `Tile` | full X/Y/Z |
| `X` / `Y` | `int` | = `Location.X/Y` |
| `XY` | `Point` | 2-D |
| `Z` | `float` | height in tiles (0.5 ≈ sitting) |
| `Direction` | `int` | body facing 0–7 (raw int, not the `Directions` enum) |
| `Dance` | `int` | 0 = none; cast to `Dances`: `None=0 Dance=1 PogoMogo=2 DuckFunk=3 TheRollie=4` |
| `IsIdle` | `bool` | AFK |
| `IsTyping` | `bool` | currently typing |
| `HandItem` | `int` | held drink/prop type ID (0 = none) — **not** an inventory ID |
| `Effect` | `int` | avatar effect ID (0 = none) |
| `CurrentUpdate` / `PreviousUpdate` | `IEntityStatusUpdate?` | **null** before first update |
| `IsRemoved` / `IsHidden` | `bool` | stale-ref guard / client-side hide |
| `Type` | `EntityType` | `User=1 Pet=2 PublicBot=3 PrivateBot=4` |

**`IRoomUser : IEntity`** adds: `Gender` (`[Flags] Male=1 Female=2 Unisex`), `GroupId` (`-1` if none), `GroupStatus`, `GroupName`, `FigureExtra`, `AchievementScore`, `IsModerator`, `RightsLevel` (from `CurrentUpdate.ControlLevel`; 0 = none), `HasRights` (`RightsLevel>0`), `BadgeRank` (Flash only). `RightsLevel`/`HasRights` are 0/false until the first status update.

**`IEntityStatusUpdate`** (`CurrentUpdate`/`PreviousUpdate`) — per-tick movement/state:

| Member | Type | Notes |
|---|---|---|
| `Index` | `int` | matches entity |
| `Location` | `Tile` | tile after this tick |
| `HeadDirection` / `Direction` | `int` | head can differ from body |
| `Status` | `string` | raw `/sit 0.5 0/mv 3,4,0.0/` |
| `Stance` | `Stances` | `Stand=0 Sit=1 Lay=2` |
| `IsController` / `ControlLevel` | `bool` / `int` | rights (`flatctrl`) |
| `IsTrading` | `bool` | `trd` fragment |
| `MovingTo` | `Tile?` | destination this tick; null if stationary |
| `SittingOnFloor` | `bool` | sit with no furniture |
| `ActionHeight` | `double?` | Z offset for sit/lay; null standing |
| `Sign` | `Signs` | shown sign or `None=-1` (`Zero=0…Ten=10 Heart=11 Skull=12 Exclamation=13 SoccerBall=14 Smile=15 RedCard=16 YellowCard=17`) |

Also implements `IReadOnlyDictionary<string, IReadOnlyList<string>>` for raw fragment access (`update["sit"]`, `update["mv"]`, case-insensitive).

**`IBot : IEntity`** adds `Gender`, `OwnerId`/`OwnerName` (`-1`/empty for public), `Data` (`IReadOnlyList<short>` skills, private only), `IsPublicBot`/`IsPrivateBot`.
**`IPet : IEntity`** adds `Breed`, `OwnerId`/`OwnerName`, `RarityLevel`, `HasSaddle`, `IsRiding`, `CanBreed`/`CanHarvest`/`CanRevive`, `HasBreedingPermission`, `Level`, `Posture`.

### IFurni / IFloorItem / IWallItem

`IItem` base of all items: `Type` (`ItemType`: `Floor='s' Wall='i' Badge='b' Effect='e' Bot='r'`), `Kind` (`int` furni type), `Id` (`long` instance ID).

**`IFurni : IItem`** (floor + wall room items): `OwnerId` (`long`), `OwnerName` (`string`), `State` (`int`, `-1` if unparseable), `SecondsToExpiration` (`int`, `-1`=never), `Usage` (`FurniUsage`: `None=0 Rights=1 Anyone=2`), `IsHidden` (`bool`, client-side only).

**`IFloorItem : IFurni`**

| Member | Type | Notes |
|---|---|---|
| `X` / `Y` | `int` | tile col / row |
| `XY` | `Point` | combined |
| `Z` | `double` | stack height (tile units) |
| `Height` | `float` | item's own visual height |
| `Direction` | `int` | `0`=N `2`=E `4`=S `6`=W (diagonals odd) |
| `Extra` | `long` | overloaded: consumable stage `0/1/2` **or** linked teleporter ID — disambiguate via `Kind` |
| `Data` | `IItemData` | structured data (see below) |
| `StaticClass` | `string` | populated only when `Kind < 0` |
| `Location` | `Tile` | X/Y/Z |
| `Area` | `Area` | footprint |

`FloorItem.State` derives from `Data.Value` via `double.TryParse` (handles `"1.0"`).

**`IWallItem : IFurni`**

| Member | Type | Notes |
|---|---|---|
| `Location` | `WallLocation` | full struct |
| `WX` / `WY` | `int` | wall segment coords |
| `LX` / `LY` | `int` | local offset in segment |
| `Orientation` | `WallOrientation` | `Left='l'` / `Right='r'` |
| `Data` | `string` | **plain string, NOT `IItemData`** |

`WallItem.State` = `int.TryParse(Data)` directly; `-1` if non-integer. Never cast `IWallItem.Data` to `IItemData`.

### IInventoryItem

`IInventoryItem : IItem` — items in your inventory.

| Member | Type | Notes |
|---|---|---|
| `ItemId` | `long` | **inventory-slot** ID — use for `GetItem`, trade `Offer(itemId)`, add/remove events |
| `Id` | `long` | in-room furni instance ID — **different value** from `ItemId` |
| `Category` | `FurniCategory` | `Unknown=0 Normal=1 Wallpaper=2 Floor=3 Landscape=4 Sticky=5 Poster=6 Trax=7 Disk=8 Gift=9 MysteryBox=10 Trophy=11 GroupFurni=17 Clothing=23` (+horse/plant) |
| `Data` | `IItemData` | structured (same interface as floor) |
| `IsRecyclable`/`IsTradeable`/`IsGroupable`/`IsSellable` | `bool` | |
| `SecondsToExpiration` | `int` | `-1`/`0` = permanent |
| `HasRentPeriodStarted` | `bool` | |
| `RoomId` | `long` | `0` if in inventory, else placed-in room |
| `SlotId` | `string` | floor items only |
| `Extra` | `long` | same overloaded field as `IFloorItem.Extra` (Flash truncates to 32-bit) |

Load via `EnsureInventory()` (blocking, must be in a room) — `G.Inventory` is null until first load and may be `IsInvalidated`.

### IItemData (floor + inventory data)

Floor and inventory `Data` is `IItemData`; wall items are NOT.

| Member | Type | Notes |
|---|---|---|
| `Type` | `ItemDataType` | discriminator (below) |
| `Flags` | `ItemDataFlags` | only `IsLimitedRare=1` |
| `IsLimitedRare` | `bool` | |
| `UniqueSerialNumber` / `UniqueSeriesSize` | `int` | LTD #x/y — meaningful only when `IsLimitedRare` |
| `Value` | `string` | legacy string repr |
| `State` | `int` | `int.TryParse(Value)`, `-1` if non-integer |

**`ItemDataType`** + subtype interface (pattern-match `Data is ...`):

| Value | Interface | Shape |
|---|---|---|
| `Legacy=0` | `ILegacyData` | single string in `Value` (most furni) |
| `Map=1` | `IMapData : IReadOnlyDictionary<string,string>` | key/value (e.g. `"state"`) |
| `StringArray=2` | `IStringArrayData : IReadOnlyList<string>` | indexed strings |
| `VoteResult=3` | `IVoteResultData` | + `Result` (`int`) |
| `Empty=4` | `IEmptyItemData` | none |
| `IntArray=5` | `IIntArrayData : IReadOnlyList<int>` | indexed ints |
| `HighScore=6` | `IHighScoreData : IReadOnlyList<IHighScore>` | + `ScoreType`/`ClearType` (`int`); each `IHighScore`: `Value` (`int`), `Names` (`IReadOnlyList<string>`) |
| `CrackableFurni=7` | `ICrackableFurniData` | + `Hits` (`int`), `Target` (`int`) |

```csharp
var item = Room.FloorItems.First(x => x.Kind == 1234);
if (item.Data.IsLimitedRare)
    Log($"LTD #{item.Data.UniqueSerialNumber}/{item.Data.UniqueSeriesSize}");
if (item.Data is IMapData map && map.TryGetValue("state", out var v)) Log(v);
if (item.Data is ICrackableFurniData c) Log($"{c.Hits}/{c.Target}");
```

### Heightmap + HeightmapTile

`G.Heightmap` (`IHeightmap?`) — **live** stacking heights, updated as furni moves. Distinct from `FloorPlan` (static model heights).

**`IHeightmap`**: `Width`/`Length` (`int`); `this[int x,int y]` and `this[(int X,int Y)]` → `IHeightmapTile` (throws OOB); `IEnumerable<IHeightmapTile>` — `foreach`-able.

**`IHeightmapTile`**:

| Member | Type | Notes |
|---|---|---|
| `X` / `Y` | `int` | coords |
| `Location` | `(int X,int Y)` | tuple |
| `IsFloor` | `bool` | true = floor tile, not void |
| `IsBlocked` | `bool` | a solid furni occupies it — **NOT** "a user stands here" |
| `IsFree` | `bool` | `IsFloor && !IsBlocked` — safe to place furni |
| `Height` | `double` | stack height in Habbo units (`(value & 0x3FFF)/256.0`); `-1` if not a floor tile |

```csharp
var t = Heightmap?[DoorTile.X, DoorTile.Y];
bool canStack = t?.IsFree ?? false;
```

**`IFloorPlan`** (static model, doesn't change with furni): `Width`/`Length`/`Scale` (`64` normal, `32` legacy)/`WallHeight` (`int`, `-1`=default), `OriginalString` (`string?`); `this[int x,int y]` → `int` height (`-1` = void); `IsWalkable(x,y)` → height ≥ 0 (also tuple/`Tile` overloads). Height chars: `0–9`→0–9, `a–w`→10–32, `x`/`X`→void.

### Tile / Point + directions

**`Tile`** (`readonly struct`): `X`/`Y` (`int`), `Z` (`float`, altitude), `XY` (`Point`). Ctors `(x,y)` (Z=0) and `(x,y,z)`. Implicit from `(int,int,float)`/`(int,int,double)` tuples. `Parse`/`TryParse` for `"(x,y,z)"`. Operators `+`/`-` with `Point` (shifts X/Y, keeps Z) or `Tile` (all three). `==`/`!=` with `Point` ignores Z; `Equals(Tile, float epsilon)` for Z comparison (Z is serialized as a string → exact float equality unreliable).

**`Point`** (`readonly struct`): `X`/`Y` (`int`); `+`/`-`, `==`/`!=`; implicit from `(int,int)` and from `Tile` (drops Z).

**`Directions`** enum (0–7, clockwise from N): `North=0 NorthEast=1 East=2 SouthEast=3 South=4 SouthWest=5 West=6 NorthWest=7`. Entity/item `Direction` is a raw `int` — cast for readability.

Related: **`Area`** (axis-aligned rect, `IEnumerable<Point>`): `X1`/`Y1`/`Width`/`Length`, derived `X2`/`Y2`/`Origin`/`Opposite`/`Size`; `Contains`/`Intersects`/`Flip`; implicit from `(int,int,int,int)` = `x1,y1,x2,y2`. **`WallLocation`**: `WX`/`WY`/`LX`/`LY`/`Orientation`; `Parse`/`TryParse`, `Flip`, `Offset`. **`WallOrientation`**: `Left='l'`/`Right='r'`, `IsLeft`/`IsRight`, implicit to/from `char`.

### FurniData & ExternalTexts (identifier ↔ kind ↔ name)

`G.GameData.Furni` (`FurniData?`, **null** until loaded — await `WaitForLoadAsync` or check) and `G.GameData.Texts` (`ExternalTexts?`).

**`FurniData : IReadOnlyCollection<FurniInfo>`** — identifier/kind catalogue:

| Method | Returns | Notes |
|---|---|---|
| `this[string identifier]` | `FurniInfo` | throws if missing |
| `GetInfo(ItemType,int kind)` / `GetInfo(IItem)` / `GetInfo(string)` | `FurniInfo` | throws if not found |
| `TryGetInfo(…, out FurniInfo?)` | `bool` | non-throwing |
| `Exists(ItemType,int)` / `Exists(IItem)` / `Exists(string)` | `bool` | identifier lookup case-insensitive |
| `FloorItemExists(int)` / `WallItemExists(int)` | `bool` | |
| `GetFloorItem(int)` / `GetWallItem(int)` | `FurniInfo` | throws |
| `FindItems/FindFloorItems/FindWallItems(string)` | `IEnumerable<FurniInfo>` | by display name |
| `FindItem/FindFloorItem/FindWallItem(string)` | `FurniInfo?` | best match or null |

Properties: `FloorItems`/`WallItems` (`IReadOnlyCollection<FurniInfo>`), `Count`.

**`FurniInfo`** (record, `init`-only):

| Property | Type | Notes |
|---|---|---|
| `Type` | `ItemType` | `Floor`/`Wall` |
| `Kind` | `int` | numeric type ID used in packets |
| `Identifier` | `string` | unique key, e.g. `"throne"` |
| `Name` / `Description` | `string` | display |
| `DefaultDirection` | `int` | 0–7 |
| `XDimension` / `YDimension` | `int` | tile footprint |
| `OfferId` / `RentOfferId` | `int` | `-1` = not sold |
| `BuyOut` / `RentBuyOut` / `IsBuildersClub` / `ExcludedDynamic` | `bool` | |
| `Category` / `CategoryName` | `FurniCategory` / `string` | `CategoryName` JSON/Unity only |
| `CanStandOn` / `CanSitOn` / `CanLayOn` / `IsUnwalkable` | `bool` | |
| `Line` | `string` | furni line, e.g. `"xmas2023"` |
| `PartColors` | `ImmutableArray<string>` | |
| `Environment` / `IsRare` | `string` / `bool` | JSON/Unity only (empty/false on Flash) |
| `Revision` / `CustomParams` / `AdUrl` | `int`/`string`/`string` | |

The `"poster"` identifier covers all poster variants — the variant is the data value, not the kind.

**`ExternalTexts : IReadOnlyDictionary<string,string>`** — `this[key]` (throws), `ContainsKey`, `TryGetValue`, `Keys`/`Values`/`Count`. Name-resolution extension methods:

| Method | Key pattern | Returns |
|---|---|---|
| `GetBadgeName(code)` / `TryGetBadgeName(code, out)` | `badge_name_{code}` | `string?` / `bool` |
| `GetBadgeDescription(code)` / `TryGetBadgeDescription` | `badge_desc_{code}` | |
| `GetEffectName(int id)` / `TryGetEffectName` | `fx_{id}` | |
| `GetEffectDescription(int id)` / `TryGetEffectDescription` | `fx_{id}_desc` | |
| `GetHandItemName(int id)` / `TryGetHandItemName` | `handitem{id}` | |
| `GetHandItemIds(string name)` | reverse scan | `IEnumerable<int>` |
| `TryGetPosterName(variant, out)` / `TryGetPosterDescription` | `poster_{variant}_name` / `_desc` | `bool` |

Item-level extension methods resolve these automatically: `item.GetInfo()`, `item.GetIdentifier()`, `item.GetName()` (falls back to `"Type:Kind"`), `item.TryGetName(out)`, `item.GetVariant()`, `item.GetCategory()`, `item.GetLine()`.

```csharp
var furni = G.GameData.Furni!;
var info = furni.GetInfo(ItemType.Floor, someItem.Kind);
Log($"{info.Identifier} \"{info.Name}\" {info.XDimension}x{info.YDimension}");
string? badge = GetBadgeName("BGHC"); // G member; prepends badge_name_ and looks it up in Texts
```

**Cross-client notes:** `IFloorItem.State` uses `double.TryParse`, `IWallItem.State`/`IItemData.State` use `int.TryParse`. `Extra` is 64-bit on Unity, truncated to 32-bit on Flash. `CategoryName`/`Environment`/`IsRare` on `FurniInfo` are populated only from JSON (Unity), empty/false on Flash/XML.

I have enough verified ground truth. Now I'll produce the section.

## Recipes & proven patterns

Distilled from the 213 user scripts. Every member below is grounded in `G.*` / `Xabbo.Core`. Loop idiom is universal: `while (Run) { try { … } catch {} Delay(n); }` where `Run => !Ct.IsCancellationRequested`. Pass `Ct` into every `await Task.Delay(ms, Ct)` and `DelayAsync` already does this for you.

### Cross-cutting primitives

| Need | Call | Notes |
|---|---|---|
| Block packet in intercept | `e.Block()` | Call **synchronously** before any `await` — the send decision is made before the first suspension |
| Multi-header intercept | `OnIntercept((In["A"], In["B"]), e => e.Block())` | Tuple → `HeaderSet`; one handler, many headers |
| Raw/unmapped send | `Send(new Packet(new Header(Destination.Server, (short)n), Client))` | Use when no `Out["Name"]` resolves on this client |
| Check header mapped | `Messages.TryGetHeaderByValue(Destination.Server, Client, i, out Header h)` | Distinguish mapped vs unmapped before probing |
| Keep script alive | `Wait()` (`= Delay(-1)`) | For pure event-driven scripts |
| Blocking sleep / async sleep | `Delay(ms)` / `await DelayAsync(ms)` | `Delay` blocks the loop thread; both honor `Ct` |
| Self projected position | `(Self.CurrentUpdate.MovingTo ?? Self.Location).XY` | True in-transit tile, not stale `Self.Location` |
| Random free tile | `Rand(Heightmap.Where(t => t.IsFree))` | `Rand(collection)` picks a random element |
| Progress label / debug | `Status(msg)` / `Log(obj)` | Non-intrusive UI status vs scripter console |

`Point` is a value type with an implicit `(int,int)` tuple cast, so `HashSet<Point> tiles = new() { (13,13),(14,13) }` works directly. At least 6 movement scripts paste the same `Point : IEquatable<Point>` boilerplate at the top.

---

### Game/dodge bots & smart movement

**Self-position tracking via `UserUpdate` + `/mv` parse.** The authoritative move target lives in the action string. Match `Self.Index`, regex `/mv X,Y,Z/`; `CultureInfo.InvariantCulture` is mandatory for the Z float (locale comma is a real bug).
```csharp
Regex mvRegex = new(@"/mv (\d+),(\d+),([\d\.]+)/");
OnIntercept(In["UserUpdate"], e => {
    int n = e.Packet.ReadInt();
    for (int i = 0; i < n; i++) {
        int idx = e.Packet.ReadInt();
        e.Packet.ReadInt(); e.Packet.ReadInt(); e.Packet.ReadString(); // x y z
        e.Packet.ReadInt(); e.Packet.ReadInt();                        // head/body rot
        string act = e.Packet.ReadString();
        if (idx == Self.Index) { var m = mvRegex.Match(act); /* update target */ }
    }
});
```
*Exemplars:* ShowMoveMent, ColorSolver4, MovementTrackerSpeed, clickdetector.

**Threat tracking via `WiredMovements`.** Per movement: skip lead int, read `FromX/FromY/ToX/ToY`, two height strings, id, skip two ints. Velocity = `(toX-fromX, toY-fromY)`; extrapolate N frames, expand by 1–2 tiles.
```csharp
OnIntercept(In["WiredMovements"], e => {
    int cnt = e.Packet.ReadInt();
    for (int i = 0; i < cnt; i++) {
        e.Packet.ReadInt();
        int fx=e.Packet.ReadInt(), fy=e.Packet.ReadInt(), tx=e.Packet.ReadInt(), ty=e.Packet.ReadInt();
        e.Packet.ReadString(); e.Packet.ReadString();
        int id = e.Packet.ReadInt(); e.Packet.ReadInt(); e.Packet.ReadInt();
    }
});
```
*Exemplars:* AntiCollisionGood, AvoidCollision2, Obsidian Maze, script-49 (full A* dodge), Sphere Runner V2–V5.

**Walkable-tile precompute (8-dir, diagonals always legal).** Build a `Dictionary<Point,List<Point>>` adjacency once at startup; never recompute per frame. Parse `FloorPlan` dynamically because the API varies — try `fp.Heightmap` (void = `'x'`) and `fp.Tiles` (blocked = sentinel `250`) in **separate** try/catch.

**Anticipated-position + danger scoring.** `getpos()` returns confirmed `/mv` target, else last command if sent <250ms ago, else `Self.Location`. Score candidate moves: `danger1` skip, `danger2` −800..−1000, `danger3` −400..−500, safe exits +20..+50 each, distance to goal −10..−100/manhattan. Stuck detection: if `hist[0]==hist[2] && hist[0]!=hist[1]` → oscillating; after >2 pick a random/less-dangerous escape neighbor.

**Furni-state-gated movement (mazes).** `Dictionary<Point, TileAction>` keyed on `RealPos`, polled every ~100ms; `TileAction` holds main/else/`Func<bool>` condition checked against live furni (`FloorItems.First(f => f.Id==X).State==0` / `.Direction==4`). Actions: `Move(x,y)`, `UseGate(furniId)` (→ `Out.EnterOneWayDoor`), `Talk(":exit")`.
*Exemplars:* MoveAction, Obsidian Maze, ObsidianMazeEasy, BanzaiTele-Bot, MazeRoom1.

**Reaction/dodge from a held-item packet.** `In["CarryObject"]` → `(userIndex, carryingId)`; on host holding item 3, `Move(safeX, safeY)`.
*Exemplar:* nervous game.

---

### AI chat bridges (LLM over chat)

**Trigger + throttle + typing skeleton.** Room chat starting with `+`. `OnChat(async e => …)`; `e.Message` is text, `e.Entity` is the speaker (`IRoomUser`).
```csharp
OnChat(async e => {
    if (throttled || !e.Message.StartsWith("+")) return;
    if (DateTime.UtcNow - lastAsk < cooldown) { Sign(17); return; }
    if (HasBannedWords(e.Message)) return;
    lastAsk = DateTime.UtcNow;
    Send(Out["StartTyping"]);
    var profile = await Task.Run(() => GetProfile(e.Entity.Id)); // GetProfile blocks → offload
    var reply = await CallLlm($"{persona} {Buildstate(e.Entity, profile)}", e.Message[1..]);
    Send(Out["CancelTyping"]);
    Shout(Sanitizenumbers(reply), 1014); // bubble 1014 is the de-facto default ("talkbuble")
});
```
*Exemplars:* 1Gpt-Smart(+Deepseek), ChatGpt V4, SmartAss V3, Gemini family, Ollama AI, Grok ChatGPT.

**HTTP call shape (OpenAI-compatible).** Same body for OpenAI/DeepSeek/Grok/Ollama (`localhost:11434/v1/chat/completions`); Gemini uses REST `candidates[0].content.parts[0].text`. Always `using var client = new HttpClient()` per call, `System.Text.Json` only (no Newtonsoft). Dual timeout guard:
```csharp
using var cts = new CancellationTokenSource(18000);
var task = client.PostAsync(endpoint, content);
if (await Task.WhenAny(task, Task.Delay(18000, cts.Token)) != task) return "timeout";
```

**Context builder (`Buildstate`/`roomfacts`).** Inject live state into the system prompt. `Gender`/`HasRights` are **not** on `IEntity` — read via reflection: `e.Entity.GetType().GetProperty("Gender").GetValue(e.Entity)`. Profile fields are `dynamic`: `.Friends` (`== -1` ⇒ hidden, guard before reading the rest), `.ActivityPoints`, `.Created`, `.LastLogin`, `.Level`, `.StarGems`, `.IsFriend`. Pull room facts from `Room.Name`, `Room.FloorItems.Count()`, `Users` (`'{Name}':'{Motto}':'{Gender}'`).

**Per-user chat ring buffer.** `Dictionary<string,List<string>>`, cap 10 (some keep 5/35/45); compute the joined log **inside** the handler — computing `formattedChatLog` once at startup is a real bug (always empty).

**Output hardening (all bots).**
- Unicode allowlist (keep Latin + DE/PT/ES accents + punctuation + brackets for `[CMD]`): `Regex.Replace(answer, @"[^a-zA-Z0-9\s\p{P}äöüÜÄÖß+=ÀàÃãÇçÉéÊêÍíÓóÔôÕõÚúÜü\[\]]", "")`.
- Long-number masking (anti-flag): split runs of ≥5 digits with `x` — `Regex.Replace(t, @"\d{5,}", m => string.Join("x", Enumerable.Range(0, m.Length/5).Select(i => m.Value.Substring(i*5,5))))`.
- Strip `<think>…</think>` for reasoning models (Ollama/DeepSeek).

**In-band action protocol.** Parse `[CMD:DANCE]` / bare `[DANCE]` from the reply, dispatch, then strip tags before `Shout`. Verified action calls: `Dance(1)`, `Wave()`, `Sit()`/`Stand()`, `Trade(target.Index)`, `Send(Out["IgnoreUser"], id)`. `[LASER]` → `Talk(":yyxxabxa")` (client effect cheat code). Newer V3/V4 use a typed mini-DSL `(command:"Move",i:{x},i:{y})` parsed by regex → `Send(Out["Move"], …)`.

**Flood/mute sign-loop.** Read duration int, set `throttled`, show a sign until elapsed, then clear:
```csharp
OnIntercept(In.FloodControl, async e => {
    int dur = e.Packet.ReadInt(); throttled = true;
    var t0 = DateTime.Now;
    while (DateTime.Now - t0 < TimeSpan.FromSeconds(dur)) { Sign(16); await DelayAsync(2000); }
    throttled = false; Sign(15);
});
// In.MuteTimeRemaining: same shape, Sign(12)
```
Signs: 16 flood, 12 mute, 15 cleared, 17 cooldown, 11 love, 13 broadcast.

**DM bridge.** Auto-accept (`In["NewFriendRequest"]` → `int userId, string name` → `AcceptFriendRequests(new[]{userId})`), then `SendMessage(id, text)`. Long replies chunk at 125 chars with 500ms gaps. Echo your own DM into your client with `Send(In.MessengerNewConsoleMessage, userId, "> "+text, 0, "")`.

**Avatar mimic.** `Send(Out["UpdateFigureData"], "M", e.Entity.Figure)`, wait 8500ms (no confirm event), restore. Note: hardcoded `"M"` copies female avatars as male — a known wart.

*Other bridges:* Discord webhook (`habbo to discord`, HTC_DC_SCRIPT) forwards `In.Chat` to a webhook; Base64Bot (HTC_BASE64Bot) encodes outgoing / decodes incoming in try/catch; Grok UserImage builds the avatar image URL from the figure string for vision models.

---

### Mass furni placement / use

| Task | Pattern |
|---|---|
| Place from inventory | `EnsureInventory(); foreach (var i in Inventory.Named("X")) { Place(i, tile.Location); Delay(35-150); }` |
| Bulk place w/ rotation | `Place(roller, new Point(7,10), rot); rot += 2;` |
| Auto-pickall threshold | `if (++placed >= 53) { Talk(":pickall"); placed = 0; }` (BC: `Shout(":pickallbc", 0)`) |
| Mass use/toggle | `foreach (var f in FloorItems.Where(x => x.GetName()==name)) Send(Out["PresentOpen"], (int)f.Id); Delay(1000);` |
| Wall placement | `Move(wallItem, $":w=X,Y l=X,Y l")` / `Send(Out["PlacePostIt"], id, ":w=0,7 l=8,27 l")` — colon prefix mandatory |
| BC mass place | `Send(Out.BuildersClubPlaceRoomItem, categoryId, offer.Id, "", x, y, dir)` |
| Recycle | `Send(Out["RecycleItems"], 8, id0..id7)` — exactly 8 IDs as separate args, IDs negated `-(int)i.Id` |

Inventory load: `EnsureInventory(timeoutMs)` (required before any `Inventory.*`) or low-level `new Xabbo.Core.Tasks.GetInventoryTask(Interceptor, true).Execute(60000, Ct)`. Filter by extra data: `(item.Data as MapData)?["rarity"] == "0"`. **Always block server noise** during automation: `OnIntercept((In.ErrorReport, In["MarketPlaceOffers"], In["MarketplaceMakeOfferResult"], In["FurniListInvalidate"], In["UnseenItems"]), e => e.Block())`. Throttle 35–300ms/op (35–150 floor, 200 wall, 1s social) or the server drops items.

*Exemplars:* PlaceSeedRoom, PlaceSeedToRoomSimple, PlacePlantPickupSeed, OpenBox, Recycler, placebc, PlaceBCWall, POST-IT-LEVEL, UseFloorItemMass.

**Pet breeding loop.** Suppress pet/inventory spam, then pair customize + breed:
```csharp
OnIntercept((In.PetBreedingResult, In.PetStatusUpdate, In["FurniListInvalidate"], In["UnseenItems"]), e => e.Block());
while (Run) foreach (var pet in Pets) {
    Send(Out["CustomizePetWithFurni"], potionId, pet.Id); Delay(500);
    Send(Out.BreedPets, 0, pet.Id, pet.Id); Delay(1000);
}
```
*Exemplars:* BreedSeed(V2), SimpleBreedV1.

---

### Room / catalog / market scanning & scraping

**Catalog walk.** `GetCatalog()` (or `GetBcCatalog()`) → nodes; per node `GetCatalogPage(node)` (network call, pace 150–300ms), flatten `page.Offers → offer.Products`, match `product.GetIdentifier()`. Distinguish currency via `offer.PriceInActivityPoints` / `ActivityPointType`. Serialize with `JsonSerializer` + `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to avoid escaping UTF-8.
```csharp
foreach (var node in GetCatalog().Where(x => x.Id > 0)) {
    Status($"…"); var page = GetCatalogPage(node);
    foreach (var offer in page.Offers) foreach (var p in offer.Products)
        if (p.GetIdentifier() == target) { /* hit */ }
    await Task.Delay(300, Ct);
}
```
*Exemplars:* CatalogScraper, placebc, PlaceBCWall (joins `furnidata_json/0` for colors).

**Marketplace.** `SearchMarketplace("", 1, 10, MarketplaceSortOrder.LowestPrice, 15000)` → `IMarketplaceOffer` (`.Id` cast to `int`, `.Price`); buy with `Send(Out.MarketplaceBuyOffer, (int)offer.Id)`. Sniper polls `SearchMarketplace(info.Name).OfKind(id)` and buys under threshold every ~400ms. Stats sweep: `Send(Out.GetMarketplaceItemStats, spriteId, 1)` then `await ReceivePacket(In.MarketplaceItemStats, Ct)`, 200ms throttle. Auto-relist: on `In.MarketplaceSaleSuccess` re-send `Out.PlaceItemInMarketplace`.
*Exemplars:* ShopSniper, MarketScan, MarketSales Bot, GetOfferID.

**Multi-room furni scanner.** `SearchNavByOwner(name).Where(r => r.IsOpen)`, `EnsureEnterRoom(r.Id) == RoomEntryResult.Success`, then `Furni.OfKind(FurniData.FindFloorItem(name)).Any()`; 2500ms between rooms.
*Exemplars:* seachfurniscript, MP Adventure, Claude Takeover.

**Room → HTTP bridge.** On `OnEnteredRoom` + object/user intercepts, POST room state JSON to a local endpoint; wrap each item access in try/catch (items can be null mid-enumeration); 15s polling fallback; dispose `HttpClient` on stop.
*Exemplar:* Shroom.

**Profile / user scraping.** `Send(Out["GetExtendedProfileByName"], name, false)`, intercept `In["ExtendedProfile"]`, read fields in exact order; **guard optional trailing bytes** with `packet.Length - packet.Position > 0` + per-field try/catch. Username→index map from the `Users` packet (read all entities, skip fields sequentially).
*Exemplars:* ReadUserProfile, FetchUsersDate, GetUserIndexID, UserInfo Export.

---

### Puzzle solvers

**Grid read (shared base).** Build `Dictionary<(int gx,int gy), long tileId>` at startup, then re-query `FloorItems.FirstOrDefault(f => f.Id == tileId)` to read current `item.State` — avoids re-scanning by position. Grid offset constants `OX`/`OY`. After each move, confirm with `WaitForTileChange` (poll state, ~5ms × up to 200) rather than fixed delay.

| Solver | Algorithm | Move execution |
|---|---|---|
| ColorSolver1 | randomized heuristic (8000 tries / 4s), greedy fallback | `Send(Out["Move"], x, y)` @55ms |
| ColorSolver2/3 | A* (Chebyshev, zeros=walls), `SortedSet<(f,seq,x,y)>` PQ | `Move`, poll state to confirm |
| ColorSolver4 | Fleury bridge-detection + Warnsdorff, `UserUpdate` true-pos tracking | `Move` |
| FloodIt / Sirjonas-Floodit | greedy lookahead seed → parallel IDA* (`Parallel.ForEach`, `lock` on `globalBest`, 4000ms cap) | `Send(Out["ClickFurni"], id, 0)` + `WaitForFlood` |
| B3R1 / 1B3R1 | backtracking (self-referencing `Func<bool> solve=null; solve=()=>…`, `goto found` to exit nested loops) | `ClickFurni` ×2 (pick up) + `Move` to target |
| Domino V1→V4 | greedy → backtracking → constraint solver; board synced via `In["ObjectAdd"]`; clear board on `ObjectRemove` (v3 fix) | place via packets |
| Tetris/Tetris2 | column scoring (line-clears − holes) | render dirty cells only via `Send(Out["UseFloorItem"], id, state)` |
| Snake | `LinkedList<(x,y)>` body, place/remove floor item per tick | — |

**Pixel art / color paint.** Detect color from furni name suffix `Regex.Match(name, @"\d+$")`. Sort target tiles by color to minimize selector switches: click selector furni only when color changes, then click each target tile (50ms gaps). Source area maps to field area by `offsetY = SRC_MIN_Y - FIELD_MIN_Y`. 3-attempt re-scan to catch misses.
*Exemplars:* Click-PixelArt(-Automatic), MakeRIPV2, SVG_Tracer, TextTracer (bitmap font `Dictionary<char,int[]>`).

**Drop-in game state (FridgeCheat etc.):** intercept the game-state packet → solve client-side → replay moves with delay.

---

### Trading automation

**Offer + accept flow.** `Trade(targetIndex)` / `Send(Out["OpenTrading"], userId)` → `Send(Out["TradingOffer"], furniId)` → `Send(Out["AcceptTrade"])` → `Send(Out["ConfirmTrade"])`, 300–500ms between steps. **Register the intercept before the request** to avoid a race.

**Bulk add.** `Send(Out.TradeAddItems, items.Select(x => (int)x.ItemId).ToList())` — one `List<int>`.

**Auto-accept loop.** `Trader.csx` waits per partner via a TCS gate:
```csharp
async Task<IPacket?> WaitPacket(Header h, int ms) {
    var tcs = new TaskCompletionSource<IPacket>();
    using var _ = ... OnIntercept(h, e => tcs.TrySetResult(e.Packet));
    try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ms), Ct); } catch { return null; }
}
```
*Exemplars:* Trade Accept, Trader, Tradepass(_Unity), PlaceToTrade.

---

### Packet tracing & header discovery

**Unmapped header probe.** Iterate a range; skip mapped via `Messages.TryGetHeaderByValue(Destination.Server, Client, i, out _)`; send the rest as `new Packet(new Header(Destination.Server, i), Client)`. Reflect over `Out` properties to recover names. Resumable scanners (`ParseUnityHeader*`) burst 50× per header @5ms, watch for a known response header, and persist a checkpoint so a disconnect resumes.
*Exemplars:* 1SendNonMappedHeaders, SendHeaderUnkownPackets, SendHeaderNotMapped, FindHiddenHeader, ParseUnityHeader(File), DangerZone.

**One-shot field sniffer.** `OnIntercept(Out.EnterRoom, e => Log($"{e.Packet.ReadInt()} {e.Packet.ReadString()}"))`. Raw hex dump: `Log($"[{e.Packet.Header}] {BitConverter.ToString(e.Packet.ToArray())}")`.

**Intercept-rewrite.** Capture a value, block, re-emit transformed: e.g. PlayFreeze maps `Out["ClickFurni"]` → `Out["UseFurniture"]`; MoveUserTo blocks an avatar click + the next `Move`, then teleports a target via negative wired variable IDs (`Send(Out["WiredSetObjectVariableValue"], 1, idx, "-270"/"-230"/"-231", val)` with 250ms gaps).

---

### Effects, dances, misc

- Action sequence: `Dance(1); Wave(); Sign(3); ThumbsUp(); Sit(); Stand(); Talk(text);` spaced by `Delay`. `Dance(0)`/`StopDancing()` stops.
- Movement effect: `ActivateEffect(id)` / `EnableEffect(id)`, restore with `… 0` after a delay.
- Friend-request DM onboarding, `MimicLook` figure spoof, `SendVisibleMessage` self-echo (see AI section — same helpers).
- Shell-out: write a temp `.js`, `Process.Start("node", …)` with `RedirectStandardOutput`, fire a game `Send` in parallel, then `WaitForExit` and inspect stdout for a known prefix (Minter). Embedded `HttpListener` + inline HTML UI controls a batch loop and auto-opens the browser (Recycler).

---

### Pitfalls (verified, recurring)

| Pitfall | Fix |
|---|---|
| `e.Block()` decided after an `await` | Call it on the first synchronous line of the handler |
| Z-float parsed with locale comma | `double.Parse(s, CultureInfo.InvariantCulture)` |
| `formattedChatLog` computed once at startup | Build the joined log inside `OnChat` |
| `Log($"Timeout for {e} seconds")` logs the event object | Log the duration int |
| `var extravar = $"…{Language}"` before `var Language=…` | Declare `Language` first (forward-ref → empty string) |
| `GetProfile`/inventory blocking the chat handler | `await Task.Run(() => GetProfile(id))`; `EnsureInventory` before `Inventory.*` |
| `UserUpdate` is a batch packet | Loop all entities, skip every field even for non-targets |
| Stale `Self.Location` mid-walk | `Self.CurrentUpdate.MovingTo ?? Self.Location` |
| Reading past packet end on optional fields | Guard `packet.Length - packet.Position > 0` |
| Sending furni-from-inventory with positive ID | Negate: `-(int)item.Id` |
| Too-fast sends → server drops/kick | 35–150ms furni, 200ms wall/market, 1s social, 50ms scan |
| Flash vs Unity arg-count divergence | branch on `Client` / `Session.Is(ClientType.Unity)` |

## Debugging & issue-spotting playbook

### Triage tools (read in this order)

| Tool | Returns | When to use |
|---|---|---|
| `get_errors` | Roslyn compile diagnostics `(line,col): error CSxxxx: message` | First thing after any `edit_tab`/`run_code` that fails to start. Pure compile-phase. |
| `get_script_status` | `ScriptStatus` enum + `IsFaulted` | Confirm whether a run reached `Running`, ended `Complete`, or died `CompileError`/`RuntimeError`/`TimedOut`/`Canceled`/`Aborted`. |
| `get_script_log` | Per-script output: `Log(...)`/`Status(...)` lines, return value, runtime exception (script frames only, `filename:line N`) | Diagnose runtime faults. `Xabbo.*` stack frames are filtered, so only your `.csx` frames show. |
| `get_app_log` | Host/interceptor-level log | Connection drops, interceptor not attached, data (FurniData/FigureData) load failures, things that never reach your script. |

`ScriptStatus` decoder: `CompileError`→read `get_errors`; `RuntimeError`(`IsFaulted=true`)→`get_script_log` for the exception; `TimedOut`→an `OperationCanceledException` fired without you cancelling (a `Receive`/`Ensure*` blew its timeout, OR host killed a stuck script); `Canceled`→you called `Finish()` or the user stopped it; `Aborted`→thread hard-killed.

### Compile errors → cause → fix

| Error | Cause | Fix |
|---|---|---|
| `CS0103: The name 'X' does not exist` | Member not on `G`, or namespace not imported | Verify against the API; default imports already cover `System.Linq`, `Xabbo.Core`, etc. — don't add `using` for those. |
| `CS1061: 'IFloorItem' has no definition for 'GetName'` | Extension method needs its type arg | `item.GetName(FurniData)` — pass `FurniData`; extensions live in `Xabbo.Core.Extensions` (imported). |
| `CS0246: type 'Point' not found` | Used `Point` without defining it | `Point` is **not** built in. Either use `Xabbo.Core` `Tile`/`(int,int)` tuples, or paste the custom `Point` struct (see Idioms). |
| `CS1503: cannot convert 'int' to 'Header'` | Passed a raw short where a `Header` is expected | Use `In.Chat`/`Out.Move` or `In["WiredMovements"]` indexer, never a bare number. |
| `CS4033 / CS1996: await in non-async` | Used `await` in a sync lambda passed to `OnIntercept`/`OnChat` | Make the lambda `async e => { ... }` — the `Func<...,Task>` overload exists for every `On*`. |
| `CS0815/CS8130: cannot assign void` | Assigned a `void` G method (`Move`, `Talk`) to a var | These return `void`; only `Receive`/`Get*`/`Search*` return values. |
| `CS0019: operator '==' cannot be applied to 'Point'` | Custom `Point` missing operators | Paste the full struct with `==`/`!=`/`Equals`/`GetHashCode`. |

### Runtime faults & pitfalls → fix

| Symptom | Root cause | Fix |
|---|---|---|
| `NullReferenceException` on `Room`/`Self`/`Heightmap` | Not in a room; `Room` is `IRoom?`, `Self` is `IRoomUser?`, `RoomId == -1` | Guard: `if (!IsInRoom \|\| Self == null) { Log("not in room"); return; }`. `Self` is `null` until entities load even after entering. |
| Script status stuck `Running`, won't stop | `Ct` not honored: tight loop or blocking call ignores cancellation | Loop on `while (Run)` (= `!Ct.IsCancellationRequested`); use `Delay(ms)` (throws on cancel) not `Thread.Sleep`; pass `Ct`/timeout to every async op. `RunTask` bodies must poll `Run` + `Delay` as exit points. |
| `TimedOut` immediately on `EnsureInventory`/`Receive`/`Get*` | Server never replied: not in room, wrong header, or feature N/A | `EnsureInventory` needs you in a room. For `Receive`, confirm the header matches the actual incoming packet and `Client`. Bump `timeout` or use `TryReceive` (returns `false` on timeout, but `Ct` still throws). |
| `await` "does nothing" / fires after script ends | Async G call not awaited (`SendAsync`, `DelayAsync`, `ReceiveAsync`) returns `Task`/`ValueTask` discarded | `await` it, or use the sync overload (`Send`, `Delay`, `Receive`). |
| Chat sent but emoji/special chars missing | Server strips non-allowlisted chars from `Talk`/`Shout` | Pre-sanitize: `Regex.Replace(text, @"[^a-zA-Z0-9\s\p{P}äöüÜÄÖß+=]", "")`. Long digit runs also flagged — mask `\d{5,}`. |
| `Receive`/intercept reads garbage or throws on `ReadX` | Wrong field type/order, or Flash↔Unity layout differs | Read fields in exact wire order with the right `ReadInt`/`ReadString`/etc. Float coords are sometimes `string` (e.g. `UserUpdate` z is `ReadString`). |
| Same code, different packet shape per client | `Client` is `Flash`/`Unity`/`Shockwave`; layouts diverge (e.g. `PlaceFloorItem`) | Prefer G helpers (`PlaceFloorItem`, `PlaceWallItem`) which branch on `Client` internally. For raw packets, branch on `Client` yourself; resolve headers via `In["Name"]`/`Out["Name"]` not hardcoded shorts. |
| Header not found / `TryGetHeaderByValue` false | Unmapped or wrong `Destination`/`Client` | `Messages.TryGetHeaderByValue(Destination.Server, Client, short, out var h)` to test; named headers (`Out.Chat`) are safest. |
| Float parsed wrong (1.5 → 15 or throws) | Locale comma/dot mismatch | Always `double.Parse(s, CultureInfo.InvariantCulture)`. |
| Sent packet looks injected to client but hits server (or vice-versa) | Direction confusion | `Send(Out.*)`→server; to fake an **incoming** packet to the client use `Interceptor.Send(In.*, ...)` (e.g. `ShowBubble`, fake DM echo via `In.MessengerNewConsoleMessage`). |
| Disconnect / flood mute mid-loop | Too-fast sends (chat, moves, purchases) trip server rate limits | Throttle: `Delay()` between sends (chat ≥ ~1.5s); guard with `DateTime.UtcNow - last < ratelimit`. Handle `In.FloodControl`/`In.MuteTimeRemaining` (carry a duration `int`) and back off. |
| Exception in hot loop kills script | Unguarded `ReadX` on malformed packet at 50Hz | Wrap the per-iteration body in `try { ... } catch { }` and `Delay(20)` — standard collision-bot pattern. |
| `UserDuckets` wrong | Returns hardcoded `10` when ducket point type absent | Don't trust it as a balance; read `UserPoints[ActivityPointType.Ducket]` directly and handle missing. |
| `SetUserFigure(string)` throws | Figure resolves to `Unisex` gender | Use the 2-arg `SetUserFigure(figure, gender)` overload. |
| Event handler logs the wrong value | Logging the event object instead of the parsed field (e.g. `Log($"...{e}...")`) | Log the extracted local (`Log($"...{duration}...")`), not `e`. |
| `FloorPlan.Tiles` vs `.Heightmap` throws | Property availability depends on client | `dynamic fp = FloorPlan;` then `try { x = fp.Tiles; } catch {}` and `try { y = fp.Heightmap; } catch {}` — handle whichever exists. Heightmap void tile = `'x'`; Tiles blocked sentinel = `250`. |

### Probe loop (fast iterate without polluting tabs)

1. **Probe with `run_code` (hidden)** — run a throwaway snippet to inspect live state before committing to a tab:
   ```csharp
   Log($"in={IsInRoom} room={RoomId} self={Self?.Index} client={Client}");
   Log($"users={Users.Count()} furni={Furni.Count()}");
   var p = Receive(In.Whisper, timeout: 5000, block: true);
   Log(p.ReadString());
   ```
   Read result via `get_script_log`. Hidden runs don't create a saved tab — use them to discover packet layouts, confirm headers, dump `ToJson(...)` of an object, or check `get_errors` on a fragment.
2. **Confirm packet shape** before trusting it: probe a single `Receive`, `Log` each `ReadX` in order, adjust types until the dump is sane.
3. **Promote to a tab** with `edit_tab` once the snippet works, then **rerun**. On failure: `get_errors` (compile) → fix → `edit_tab` → rerun; `get_script_status`+`get_script_log` (runtime) → fix → `edit_tab` → rerun.
4. **Keep loops cancel-safe** while iterating: always `while (Run)` + `Delay`, so a stuck probe is stoppable instead of forcing an `Aborted` hard-kill.

I have all the ground truth I need. Producing the section now.

## Cheat sheet & gotchas

> Everything below is a direct member of the globals class `G` (or a `Xabbo.Core` type) and is in scope unqualified inside a `.csx` script. Members marked **blocking** run synchronously and may throw on timeout/cancellation.

### Top one-liners (member → use)

| Member | Use |
|---|---|
| `Run` | Loop guard; `false` once cancelled. `while (Run) { ... }` |
| `Delay(ms)` / `Delay(TimeSpan)` | **Blocking** sleep; throws `OperationCanceledException` on cancel (this is the cooperative exit point) |
| `DelayAsync(ms)` | Awaitable sleep (returns `Task`) |
| `Wait()` | Park script alive forever (`Delay(-1)`) — use to keep intercept/event handlers running |
| `Finish()` | Cancel the script from inside a callback; sets `IsFinished` |
| `throw Error("msg")` | Throw a `ScriptException` whose message shows in the log |
| `Log(obj)` / `Log()` / `Status(obj)` | Write to output / set status line |
| `ToJson(o)` / `FromJson<T>(s)` | (De)serialize |
| `RunTask(action)` | Fire work on thread pool — **you** must honor `Run`/`Delay` to stop it |
| `Distance(a, b)` | Euclidean distance between two `Point`s (static) |
| `InitGlobal(name, value)` | Cross-script persistent var (returns `true` if created) |
| `Self` | Your own `IRoomUser` in room (`null` if not in room) — `Self.Index`, `Self.Location` |
| `UserId` / `UserName` / `UserData` | Local account id/name/profile (throws if profile not loaded) |
| `UserCredits` / `UserDiamonds` / `UserDuckets` | Wallet (throw if not loaded; `UserDuckets` swallows → returns 10) |
| `Move(x, y)` / `Move(Point)` | **Fire-and-forget** walk request; server drives steps, no arrival confirm |
| `LookTo(x, y)` / `Turn(Directions.North)` | Face a tile / face a direction without moving |
| `Talk(msg)` / `Shout(msg)` / `Whisper(user, msg)` | Chat (`bubble` int optional) |
| `Chat(ChatType, msg, bubble)` | Low-level chat |
| `ShowBubble(msg)` | **Client-side only** fake bubble (injects an incoming packet) |
| `Action(Actions.Wave)` / `Wave()` / `Idle()` / `ThumbsUp()` | Expressions |
| `Sit()` / `Stand()` / `Dance(Dances)` / `StopDancing()` / `Sign(Signs)` | Posture/dance/sign |
| `Entities` / `Users` / `Pets` / `Bots` | Room enumerables (empty if not in room, never null) |
| `GetUser(name\|index\|id)` / `GetBot(...)` / `GetPet(...)` | Lookups → `null` if absent |
| `Respect(user)` / `Ignore(name)` / `FriendRequest(name)` / `Scratch(pet)` / `Mount(pet)` | User/pet actions |
| `Furni` / `FloorItems` / `WallItems` | Room furni enumerables (empty if not in room) |
| `GetFloorItem(id)` / `GetWallItem(id)` | Furni by id → `null` |
| `UseFurni(f)` / `ToggleFloorItem(id, state)` / `UseGate(id)` | Interact / set state / one-way gate |
| `Place(invItem, point, dir)` / `Move(floorItem, point, dir)` / `Pickup(furni)` | Place/move/pickup |
| `EnsureInventory()` | **Blocking**, long-timeout; requires being in a room. Returns `IInventory` |
| `Trade(user)` → `Offer(item)` → `AcceptTrade()` → `ConfirmTrade()` | Trade flow (see gotcha) |
| `EnsureEnterRoom(id)` | **Blocking**, confirmed entry → `RoomEntryResult` |
| `Send(packet)` / `Receive(headers, timeout, block)` / `OnIntercept(header, e => …)` | Raw protocol I/O |
| `In` / `Out` | Header tables: `In.Whisper`, `Out.Chat`, `In["WiredMovements"]` |
| `Client` | `ClientType.Flash` or `ClientType.Unity` — branch when wire format differs |

### Units & coordinate conventions

- **Tiles:** `X` = column (west↔east), `Y` = row (north↔south), both `int`. `Point(x, y)`; tuples auto-convert: `Move((5, 5))`, `new HashSet<Point> { (13,13), (14,13) }`.
- **Z / height:** `IFloorItem.Z` is `double` stack height in tile units (~`0.5` per stack tile). `UpdateStackTile(id, height)` takes a `float` in tile units and is sent ×100 internally.
- **Directions:** `0..7` clockwise from North: `North=0, NorthEast=1, East=2, SouthEast=3, South=4, SouthWest=5, West=6, NorthWest=7`. Furni `Direction` typically uses even values (`0,2,4,6`).
- **Timeouts:** ms; `-1` = infinite. Defaults: most blocking calls `10000`; inventory/pet `30000`.
- **Distance:** `Distance` is Euclidean (diagonal-aware); movement itself is 8-directional.

### Sharpest gotchas

- **`Move` never awaits arrival.** There is no `WalkTo`. To know you arrived, poll `Self.Location` / `GetUser(...)` or intercept `In["UserUpdate"]`. The authoritative target is the `/mv X,Y,Z/` action string in `UserUpdate`.
- **Float parsing locale.** When parsing `Z`/heights from packet strings, always pass `CultureInfo.InvariantCulture` — a comma-locale machine will misparse `"1.5"`.
- **`Delay` is the cancellation checkpoint.** Tight loops with no `Delay`/`DelayAsync` can't be cancelled. Inside `RunTask`, call `Delay` periodically or check `Run`, or the task outlives the script.
- **`Self` is room-scoped.** `null` when not in a room; `Self?.Index` before using it. `UserId`/`UserName` are account-level and always available once the profile loads.
- **Throwing properties.** `UserData`, `UserCredits`, `UserPoints`, `UserAchievements`, `FigureData` throw if their data hasn't loaded yet. `Room`, `Inventory`, `DoorTile` return `null` instead.
- **`EnsureEnterRoom` vs `EnterRoom`.** `EnsureEnterRoom` rewrites packets in-flight and blocks for confirmation (returns `RoomEntryResult`). `EnterRoom` is fire-and-forget and only works if the navigator already loaded that room's data. Always branch on the result:
  ```csharp
  var r = EnsureEnterRoom(12345678);
  if (r != RoomEntryResult.Success) throw Error($"Entry failed: {r}");
  ```
- **`EnsureInventory` requires a room.** The server only answers inventory requests while you are in a room; calling it in the hotel view hangs to timeout.
- **Wall vs floor data are different systems.** `IFloorItem.Data` is `IItemData` (structured); `IWallItem.Data` is a plain `string`. Don't expect `.Value`/`.Type` on a wall item.
- **`State` parsing differs.** `FloorItem.State` uses `double.TryParse` (handles `"1.0"`); `WallItem.State` and `IItemData.State` use `int.TryParse`. All return `-1` when unparseable.
- **`Receive`/`OnIntercept` headers come from `In`/`Out`.** Use `In["WiredMovements"]` for headers without a typed property. `block: true` drops the packet from the client.
- **`Place`/`PlaceWallItem` are client-specific internally** (Flash sends a space-delimited string, Unity sends typed args) — `G` handles it, but if you hand-roll the `Out.PlaceRoomItem` packet you must branch on `Client`.
- **`ShowBubble` is fake.** It injects an incoming chat packet (client display only); it does not send anything to the server. Use `Talk`/`Shout` to actually speak.
- **Trade has no `EnsureTrade`.** `Trade(user)` is fire-and-forget; subscribe to `OnTradeOpened` / `OnTradeOpenFailed` (or poll `IsTrading`) before calling `Offer`. Completion requires both `AcceptTrade()` then `ConfirmTrade()` from each side; watch `OnTradeCompleted`.
- **`Move` is overloaded.** `Move(int,int)` / `Move(Point)` walk the avatar; `Move(IFloorItem, Point, dir)` / `Move(IWallItem, WallLocation)` relocate furni. Pick the right overload or you'll move the wrong thing.
- **Copy-paste `Point` only outside the room API.** `Xabbo.Core.Point` already exists and supports tuple conversion — only define a local `struct Point` if a script needs `IEquatable`/hashing semantics not on the core type, and don't shadow the core one in scripts that call `Move`.

### Idioms

```csharp
// Enter, walk, face, speak
if (EnsureEnterRoom(12345678) != RoomEntryResult.Success) throw Error("no entry");
Move(5, 5); Delay(2000); Turn(Directions.North); Talk("here");

// React to room chat until finished
OnChat(e => { if (e.Message == "stop") Finish(); else Log($"{e.Entity.Name}: {e.Message}"); });
Wait();

// Raw intercept: read a packet without a typed header
OnIntercept(In["WiredMovements"], e => {
    var p = e.Packet; int n = p.ReadInt();
    for (int i = 0; i < n; i++) { /* read fields in wire order */ }
});

// Modify room settings (must own the room)
ModifyRoomSettings(s => { s.Access = RoomAccess.Password; s.Password = "secret"; });
```

