# Xabbo Scripter - MCP Knowledgebase

Field guide for an AI driving the xabbo scripter via MCP. Built from the full source (the G API, Xabbo.Core, Xabbo.Common, the engine, the MCP server) and 213 real user scripts. Verify any member with get_api <term>; confirm a packet header resolves before sending it (an unknown Out[..]/In[..] identifier throws); inspect live state with get_room / get_self.

Sections:
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

Xabbo Scripter runs C# .csx scripts that drive a Habbo client through an interceptor. Scripts are authored via MCP tools, compiled with Roslyn, and executed against a per-run instance of the globals class G. Every public member of G is in scope with no prefix - call Send(...), Move(...), read Room, etc. directly.

Execution model - what happens per run:

- Compile: Roslyn CSharp scripting, LanguageVersion.Latest, globalsType: typeof(G). #load resolves relative to %APPDATA%/xabbo/scripter/scripts. Errors -> CompileError, reported as (line,col): error CSxxxx: msg.
- Run: Synchronous on a thread-pool thread. Fresh new G(Host, script) in a using (G : IDisposable).
- Result: Non-null last expression / returned value is formatted and logged.
- Faults: Thrown exception -> RuntimeError (line shown as filename:line N; Xabbo.* frames filtered). OperationCanceledException + cancellation requested -> Canceled; without -> TimedOut.
- Cleanup: On dispose, all OnIntercept/On* registrations are auto-removed.

ScriptStatus: None, Compiling, Running, Cancelling, Canceled, Complete, CompileError, RuntimeError, TimedOut, UnknownError, FileNotFound, Aborted

Core abstractions:

- Ct (CancellationToken) - the script's lifeline. Run is exactly !Ct.IsCancellationRequested. Use while (Run) for loops.
- Delay(ms) - canonical cancellation/exit point: throws OperationCanceledException on cancel, which the engine catches and reports cleanly. Same for Wait() (Delay(-1), blocks forever) and blocking Receive.
- Finish() - voluntary clean stop: sets IsFinished = true, cancels Ct, then throws immediately - code after it never runs. Call it from a callback to end the script.
- Error("msg") returns a ScriptException; you must throw Error("msg") to fault with a clean message.
- In / Out - header collections: Out.Chat, In.Whisper, etc. Sending on an In.* header injects toward the client (cosmetic); Out.* goes to the server.
- Global (dynamic) - a bag shared across all scripts in the session; persists between runs. Use InitGlobal(name, value) to seed it race-safely.

Directives (triple-slash comments, scanned by regex before execution; put on first lines):

```csharp
/// @name My Script
/// @group Automation
```

@name sets the tab title; @group groups it in the list.

Default imports (no using needed): System, System.Text, System.Text.RegularExpressions, System.IO, System.Linq, System.Collections, System.Collections.Generic, System.Threading.Tasks, Xabbo, Xabbo.Messages, Xabbo.Interceptor, Xabbo.Core, Xabbo.Core.Extensions, Xabbo.Scripter.Runtime, Xabbo.Scripter.Runtime.PacketTypes. Nullable is disabled.

API discovery (use these MCP tools first, then consult this doc):

- list_api - all G member signatures (the full callable surface)
- get_api <term> - matching members with docs
- get_imports - default usings + referenced assemblies

Workflow: skim list_api for the verb you need -> get_api <term> for exact signature/overloads/defaults -> write -> run -> read the log/status -> fix the reported filename:line.

Fastest path from zero: write top-level statements that call G members directly; return or log a value to see output; throw to fault. No class, no Main, no namespace.

Canonical minimal script:

```csharp
/// @name Hello
Talk("hello world");
```

Slightly richer example - wait for a room, greet each user, then stay alive intercepting chat until cancelled:

```csharp
/// @name Greeter
/// @group Demo

if (!IsInRoom) {
    Log("Not in a room - waiting for entry...");
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

Key habits: loop on while (Run) / check !Run to break; Delay() between actions (and as the exit point inside RunTask); register On*/OnIntercept handlers before Wait(); callbacks fire on the interceptor thread, so call Finish() there to unblock the waiting script thread.

## Execution & lifecycle model

Every .csx runs as a Roslyn script compiled with globalsType = typeof(G), so all public G members are in scope unqualified. A fresh G instance is built per run (new G(Host, script) in a using) and disposed when the script ends, which auto-removes every OnIntercept / On* registration. The script body executes synchronously on a thread-pool thread via .GetAwaiter().GetResult().

### Status state machine

ScriptStatus: None -> Compiling -> Running -> (Cancelling) -> terminal.

Terminal values and how they arise:

- Complete: body returned normally
- Canceled: OperationCanceledException thrown and Ct.IsCancellationRequested (external Stop or Finish())
- TimedOut: OperationCanceledException thrown without cancellation requested (a timeout fired)
- RuntimeError: any other exception; IsFaulted = true
- CompileError: Roslyn diagnostics; reported as (line,col): error CSxxxx: message
- Aborted / FileNotFound / UnknownError: engine-level failures (no source / hard abort)

### Cancellation: Ct, Run, Stop, Finish()

The single source of truth for "should I keep going" is the script CancellationToken Ct (linked to script-level + host-level CTS).

- Ct: CancellationToken - pass to every async/cancelable call you make yourself
- Run: bool - !Ct.IsCancellationRequested; the canonical loop guard
- IsFinished: bool - true only after Finish() (distinguishes voluntary stop from external Stop)
- Finish() -> void
    cancels own CTS, sets IsFinished = true, then throws OperationCanceledException at the call site; clean stop from inside a callback
- Error(string) -> ScriptException
    returns (does not throw) an exception; throw Error("msg") to fault with a clean log message and no stack trace

Stop button / host shutdown sets Ct.IsCancellationRequested. Observe it by checking Run, or by letting a blocking call (Delay, Wait, Receive) throw OperationCanceledException. Let that exception propagate - the engine classifies it as Canceled; do not catch and swallow it.

Finish() and external Stop both surface as OperationCanceledException; IsFinished is the only way to tell them apart after the fact.

Cooperative cancellation only: a tight CPU loop with no Delay / Run check will ignore Stop until it hits a cancellation point.

### Delays & keeping alive

- Delay(int ms) -> void / Delay(TimeSpan) -> void
    blocking sleep; throws OperationCanceledException on cancel - the natural loop exit point
- DelayAsync(int ms) -> Task  [async] / DelayAsync(TimeSpan) -> Task  [async]
    awaitable form
- Wait() -> void
    Delay(-1) - blocks forever until cancelled; use to keep event/intercept-driven scripts alive
- RunTask(Action) -> void
    queues work on the thread pool; the action must poll Run and use Delay as exit points or it outlives the script

```csharp
while (Run) {
    Send(Out.Wave);
    Delay(5000);
}
```

Event-driven pattern: register handlers, then park the thread. Finish() from a callback unblocks Wait().

```csharp
OnIntercept(Out.Chat, e => {
    string msg = e.Packet.ReadString();
    if (msg == "!stop") { e.Block(); Finish(); }
});
Wait();
```

### Output: return value & logging

- A non-null returned value is formatted (via ObjectFormatter) and appended to the output log - the last expression doubles as a result printout.
- Log(string) / Log(object?) / Log() append explicit lines. Status(string?/object?) updates the status-bar text, not the log.
- On RuntimeError, the message and stack trace are logged, but frames in Xabbo.* namespaces are stripped - only your script frames remain, shown as filename:line N. Throwing Error("...") / ScriptException yields a clean message with no trace.

```csharp
Send(Out.GetCredits);
var pkt = Receive(In.Credits, timeout: 5000);
return pkt.ReadInt();   // logged as the result
```

### Threading

- The script body and Delay / Wait / Receive run on the script's thread-pool thread.
- OnIntercept and On* callbacks fire on the interceptor's thread, not the script thread. Shared mutable state touched from both needs synchronization; calling Finish() from a callback is safe and unblocks the script thread.
- RunTask(Action) adds more thread-pool threads - same cancellation discipline applies.
- WPF/UI work must go through InvokeOnUiThread<T>(Func<T>) (marked [EditorBrowsable(Never)]); scripts rarely need it.

### State that is null before entering a room

These are null/sentinel until the room is fully loaded (IsInRoom == true). Room-scoped action methods call an internal RequireRoom() and throw InvalidOperationException("The user is not in a room.") when called too early.

- Room, DoorTile, Heightmap, FloorPlan: null
- Self (GetUserById(UserId)): null
- RoomId: -1
- IsInRoom: false
- Entities / Users / Pets / Bots: empty sequence

```csharp
if (!IsInRoom) {
    var r = EnsureEnterRoom(roomId);
    if (r != RoomEntryResult.Success) throw Error($"enter failed: {r}");
}
foreach (var u in Users) Log(u.Name);
```

EnsureEnterRoom blocks until loaded and returns a RoomEntryResult (Success / Full / Banned / Unknown - the InvalidPassword enum value exists but the entry task does not currently produce it; a wrong password resolves to Unknown). EnterRoom is fire-and-forget with no confirmation, so guard subsequent room access with IsInRoom or an OnEnteredRoom handler.

### Hidden background runs vs visible editor tabs

Visible editor tab:
- Trigger: user runs a .csx from the editor UI
- Caller blocking: engine runs the body to completion
- Output: Log/Status surface in that tab
- Lifecycle: same G lifecycle, Ct, auto-deregistration

Hidden background run (run_code / run_script with wait=false):
- Trigger: MCP tool starts the script detached
- Caller blocking: tool returns immediately; script keeps running until it finishes, faults, or is stopped
- Output: no tab; caller does not receive return value/log inline - use Log/Status and poll status, or run with wait=true to get the result back
- Lifecycle: identical lifecycle; differs only in who waits and where output goes

Practical consequences for a background run: if the body finishes synchronously the script ends immediately (any OnIntercept/On* handlers are torn down with it), so a background script that must keep reacting to packets has to end with Wait() (or a while (Run) loop). Conversely, when you want a one-shot result back inline, run with wait=true and return the value.

## MCP tool reference

All tools are MCP functions that drive the running scripter app. Scripts themselves are C# .csx top-level statements where every public member of globals class G is in scope. These tools author/run/inspect those scripts; they do not run inside a script.

### Discovery & meta

get_started() -> void
    Orientation + recommended workflow. One read at session start.

get_scripting_guide() -> void
    Short syntax primer: async, Ct, @name/@group directives, examples.

get_knowledgebase() -> void
    Full dense field guide (API by domain, packets/events, models, recipes, debug playbook). Read once before authoring.

list_api() -> list
    Compact index of every G member signature. The full callable surface.

get_api(search?) -> list
    Member detail: Name, Kind, Signature, Summary, IsAsync. Omit search for everything; pass a term (matches name/signature/docs) to filter. Verify exact names here before writing.

get_imports() -> list
    Default usings and referenced assemblies available to scripts.

list_mcp_tools() -> list
    Every MCP tool with description + input schema (self-discovery).

get_server_info() -> object
    Server state: running, endpoint, requestCount, sessionCount, toolCount, scripterConnected, permissions{execute,fileWrite,editor}, authRequired. Check before any run.

get_integration() -> string
    Config snippets/CLI for wiring external LLM clients. Rarely needed mid-task.

### Connection & game context

get_connection() -> object
    connected, client, clientIdentifier, clientVersion, hotel, inRoom, roomId, inQueue, ringingDoorbell. Cheap readiness check.

get_room(maxFurni?=200) -> object
    Full room snapshot: room{...}, rights{isOwner,hasRights,...}, self, users[], pets[], floorItems[], wallItems[] (furni capped at maxFurni each, with true *Count). Primary state inspector. Returns {inRoom:false} if not in a room.

get_self() -> object
    Own avatar: id, index, name, figure, position{x,y,z}, direction, isIdle, isTyping, dance, effect, hasRights, ... Throws if not in room.

get_profile() -> object
    Account: userData{id,name,figure,gender,motto}, credits, diamonds, duckets, achievementScore, homeRoom.

get_inventory() -> object
    {loaded, count} summary only.

get_errors(script?) -> object
    With script: that script's status, faulted, error, output. Without: all scripts in error/faulted state {count, errors[]}. Go-to after a run.

### Script catalog

list_scripts() -> list
    Every known script (disk + unsaved tabs): name, group, fileName, status, ...

search_scripts(query) -> list
    Case-insensitive match over name/group/code; returns matches with full code. Find prior art / reuse.

get_script(script) -> object
    Full source + status of one script (by file name +/- .csx or display name). Auto-loads from disk.

get_script_status(script) -> object
    Run state: status, running, working, faulted, runtimeMs, output, error.

get_script_log(script) -> object
    fileName, status, faulted, output (the text under the editor).

### Editor tabs

list_tabs() -> list
    Open tabs in order: index, active, name, fileName, modified, running, status. No permission needed.

get_active_tab() -> object
    Active tab + full code. Throws if none active. No permission needed.

open_script(script) -> void  [requires: editor]
    Open existing script in a visible tab and switch to it.

create_script_tab(code) -> object  [requires: editor]
    New unsaved script in a visible tab the user can watch.

edit_tab(script, code) -> void  [requires: editor]
    Replace an open tab's code live (opens it first if needed). Core of live-fix loop.

select_tab(script) -> void  [requires: editor]
    Switch active tab (opening if needed).

close_tab(script) -> void  [requires: editor]
    Close a tab.

### Execution

run_code(code, visible?=false, wait?=true, timeoutMs?=30000) -> object  [requires: execute + scripterConnected]
    Ad-hoc code. visible=false runs hidden - ideal for inspecting state and quick probes. visible=true shows a live tab.
    Returns run snapshot: status, faulted, runtimeMs, output, error, note.

run_script(script, wait?=true, timeoutMs?=30000) -> object  [requires: execute + scripterConnected]
    Compile+run an existing saved/open script. wait=false starts and returns {note:"started"}.
    Returns {note:"already running"} if already working.

cancel_script(script) -> void  [requires: execute]
    Cancel a running/compiling script (fires its Ct).

Notes: when wait=true and the script outlasts timeoutMs, you get note:"timed out waiting; still running" (not an error) - poll with get_script_status. If not connected, run tools throw and tell you to check get_server_info.

### Files

save_script(fileName, code, overwrite?=false) -> object  [requires: fileWrite]
    Persist to disk (creates or, with overwrite=true, replaces). Updates the live tab if open. Errors on existing file/name unless overwrite. Returns {saved, path, script}.

rename_script(script, newFileName) -> void  [requires: fileWrite]
    Rename file on disk. Fails if running or target name clashes.

delete_script(script) -> void  [requires: fileWrite]
    Delete from disk + remove from scripter. Fails if running.

### Autostart

list_autostart() -> list
    Configured autostart tasks: name, fileName, status, running, valid, addedAt. No permission needed.

add_autostart(script) -> void  [requires: fileWrite]
    Mark a saved script to run on connect (save it first).

remove_autostart(script) -> void  [requires: fileWrite]
    Unmark.

restart_autostart(script) -> void  [requires: fileWrite]
    Stop-if-running then run again now.

stop_autostart(script) -> void  [requires: fileWrite]
    Stop a running autostart task.

### App log

get_app_log(maxChars?=8000) -> string
    Engine/connection/error messages (tail). Use when a failure is outside a specific script (connection drop, engine error).

### Recommended end-to-end loop

1. Confirm connection - get_server_info (check scripterConnected + permissions). Fallback detail: get_connection.
2. Learn the API (first session) - get_knowledgebase, then list_api / get_api to verify exact signatures. Never guess member names.
3. Inspect context - get_room / get_self / get_profile; search_scripts to reuse existing code.
4. Probe before committing - run_code with visible=false to read live state without cluttering the UI:

```csharp
/// @name probe
var others = Users.Where(u => u.Id != Self.Id).Select(u => u.Name).ToArray();
$"{others.Length} others: {string.Join(", ", others)}"
```

5. Author - create_script_tab to show the user a live tab, or keep iterating via run_code. Use directives + honor Ct in loops:

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

6. Run - run_script / run_code. For long-running loops use wait=false (or accept the timeoutMs poll note) so the call returns.
7. Read errors - inspect the run snapshot's error, then get_errors / get_script_status for the compile/runtime message + faulting line. get_app_log for engine/connection issues.
8. Live-edit - edit_tab with new code to patch the open tab in place, then re-run_script. No save needed between iterations.
9. Persist - save_script once correct; add_autostart if it should run on connect.

### Fast-debug tool combos

- Tight fix loop: run_script -> get_errors (faulting line + message) -> edit_tab -> run_script. Repeat without touching disk.
- State recon without UI noise: run_code(visible=false, wait=true) returning a last-expression value; the value is formatted into output.
- Background task supervision: run_script(wait=false) -> poll get_script_status -> cancel_script to stop. working=true while compiling/running; faulted flags errors; runtimeMs shows elapsed.
- "Nothing ran" / connection faults: get_server_info (scripterConnected, permissions.execute) + get_app_log rather than per-script logs.
- Permission-blocked tool calls: mutating editor/file/run tools throw if the matching permission (editor / fileWrite / execute) is off - confirm via get_server_info permissions before relying on them.

## Scripting API reference (by domain)

All G members are in scope unprefixed in .csx scripts. The script holds a CancellationToken Ct (fires on cancel/abort/Finish()); pass it to all async work. Every OnIntercept/On*/Receive registration is auto-removed when the script ends. Timeouts: DEFAULT_TIMEOUT = 10000, DEFAULT_LONG_TIMEOUT = 30000 ms. Blocking calls run synchronously but honour Ct.

### Globals, Connection & Client

state: Client:ClientType, ClientIdentifier:string, ClientVersion:string, Hotel:Hotel, Ct:CancellationToken, Run:bool(!Ct.IsCancellationRequested), IsFinished:bool
state: In:Incoming, Out:Outgoing, Messages:IMessageManager
state: FigureData, FurniData, ProductData, Texts (game-data objects; throw if not yet loaded)
state: Global:dynamic (cross-script shared bag; missing keys return null)

Delay(int ms) -> void
    blocking sleep; throws OperationCanceledException on cancel - the natural loop exit point
Delay(TimeSpan) -> void
    same as above with TimeSpan
DelayAsync(int ms) -> Task  [async]
DelayAsync(TimeSpan) -> Task  [async]
Wait() -> void
    Delay(-1); blocks forever until cancelled; keeps intercept-driven scripts alive
Finish() -> void
    cancels Ct, sets IsFinished = true, then throws OperationCanceledException at call site; clean stop from inside a callback; code after it never runs
RunTask(Action) -> void
    queues work on the thread pool; body must poll Run and use Delay as exit points or it outlives the script
Log(string) -> void
Log(object?) -> void
Log() -> void
    append to output panel
Status(string?) -> void
Status(object?) -> void
    update status text in script list (not the log)
Error(string) -> ScriptException
    RETURNS an exception; does not throw; write throw Error("msg") to fault with a clean log message and no stack trace
ToJson(object?, bool indented=true) -> string
FromJson<T>(string) -> T?
InitGlobal(string, dynamic) -> bool
    creates shared var only if absent; returns true if created
InitGlobal(string, Func<dynamic>) -> bool
    factory variant; avoids building the value when key already present
Distance(Point, Point) -> double (static)
    Euclidean distance
ShowBubble(string msg, int? index=null, int bubble=30, ChatType type=Whisper) -> void
    client-side-only chat bubble injected as In.*; server never sees it; index defaults to Self?.Index ?? -1

```csharp
while (Run) {
    Status($"tick {DateTime.Now:T}");
    Delay(5000);
}
```

Gotchas:
- Finish() re-throws immediately; code after it in the same call stack never runs.
- Global is shared across all running scripts; use InitGlobal to avoid start-up races.
- Error("msg") does not throw; write throw Error("msg").
- Game-data properties throw before load; they auto-load on connect/room entry.

### Room (state & management)

state: IsInRoom:bool, IsLoadingRoom:bool, IsRingingDoorbell:bool, IsInQueue:bool, QueuePosition:int(0-based), RoomId:long(-1=none), Room:IRoom?, DoorTile:Tile?, Heightmap:IHeightmap?, FloorPlan:IFloorPlan?
permissions: IsRoomOwner:bool, CanMute:bool, CanKick:bool, CanBan:bool, CanUnban:bool(alias of IsRoomOwner)

EnsureEnterRoom(long roomId, string password="", int timeout=10000) -> RoomEntryResult
    blocking confirmed entry; manipulates packets in-flight
    result values: Success, Full, Banned, Unknown
    InvalidPassword enum value is defined but not currently produced; a wrong password yields Unknown
EnterRoom(long roomId, string password="") -> void
    fire-and-forget Out.FlatOpc; no confirmation
LeaveRoom() -> void
    sends Out.Quit
GetRoomData(long roomId, int timeout=10000) -> IRoomData
    blocking public room info
GetRoomSettings(long roomId, int timeout=10000) -> RoomSettings
    editable settings; owner only
SaveRoomSettings(RoomSettings) -> void
    sends Out.SaveRoomSettings
ModifyRoomSettings(Action<RoomSettings> updater, long? roomId=null, int timeout=10000) -> void
    fetch -> mutate -> save; defaults to current room
CreateRoom(string name, string description, string model, RoomCategory=PersonalSpace, int maxUsers=50, TradePermissions=NotAllowed) -> void
    sends Out.CreateNewFlat; no confirmation
DeleteRoom(long roomId) -> void
    sends Out.DeleteFlat
GetRights(int timeout=10000) -> IReadOnlyList<(long Id, string Name)>
    current room; throws if not in room
GetRightsFor(long roomId, int timeout=10000) -> IReadOnlyList<(long Id, string Name)>
    any room; times out if not owner

IRoom (via Room): Id, Name, OwnerName, OwnerId, Access:RoomAccess, IsOpen/IsDoorbell/IsLocked/IsInvisible, MaxUsers, Trading:TradePermissions, Score, Category, Tags, IsGroupRoom, GroupId/GroupName/GroupBadge, HasEvent, Model, Floor/Wallpaper/Landscape, DoorTile, EntryDirection, FloorPlan, Heightmap, HideWalls, WallThickness/FloorThickness:Thickness, Moderation, ChatSettings
Collections: Furni, FloorItems, WallItems, Entities, Users, Pets, Bots
Lookups: GetEntity<T>(int|string), GetEntityById<T>(long), TryGetUserByName(name, out user), GetFloorItem(long), GetWallItem(long), GetFurni(ItemType, long)

RoomAccess: Open=0, Doorbell=1, Password=2, Invisible=3, Friends=7
TradePermissions: NotAllowed=0, RightsHolders=1, Allowed=2
RoomCategory: Party=2, Games=3, FansiteSquare=5, HelpCenters=6, PersonalSpace=10, BuildingAndDecoration=11, ChatAndDiscussion=12, Trading=14, Agencies=16, RolePlaying=17
RoomEntryResult: Unknown, Full, Banned, InvalidPassword, Success

```csharp
var result = EnsureEnterRoom(12345678);
if (result != RoomEntryResult.Success) throw Error($"Entry failed: {result}");

ModifyRoomSettings(s => {
    s.Access = RoomAccess.Password;
    s.Password = "secret";
});
```

Gotchas:
- EnsureEnterRoom confirms entry (use it when correctness matters); EnterRoom is fire-and-forget for already-loaded navigator rooms.
- All room-scoped helpers call a private RequireRoom() and throw InvalidOperationException("The user is not in a room.") when not in a room.
- GetRoomSettings/GetRights need ownership; they time out otherwise.

### Movement & Navigation

Move(int x, int y) -> void
Move(Point) -> void
Move(IFloorEntity) -> void
    sends Out.Move pathfind request; entity overload picks a random tile in its Area; fire-and-forget, does not await arrival
LookTo(int x, int y) -> void
LookTo(Point) -> void
    sends Out.LookTo; face a tile without moving
Turn(int dir) -> void
Turn(Directions) -> void
    rotate in place via magic vector; dir is 0-7

Directions: North=0, NorthEast=1, East=2, SouthEast=3, South=4, SouthWest=5, West=6, NorthWest=7

Navigator methods (all blocking):

GetNav(string category, string filter="", int timeout=10000) -> NavigatorSearchResults
    raw results; has GetRooms() (dedup by id), FindRooms(name?, description?, ownerId?, owner?, access?, trading?, category?, groupId?, group?), FindRoom(name)
SearchNav(string category, string filter="", int timeout=10000) -> IEnumerable<IRoomInfo>
    GetNav(...).GetRooms()
QueryNav(string query, int timeout=10000) -> IEnumerable<IRoomInfo>
    category "query" - the "Anything" box
SearchNavByName(string roomName, int timeout=10000) -> IEnumerable<IRoomInfo>
    filter roomname:<name>
SearchNavByOwner(string ownerName, int timeout=10000) -> IEnumerable<IRoomInfo>
    filter owner:<name>
SearchNavByTag(string tag, int timeout=10000) -> IEnumerable<IRoomInfo>
    filter tag:<tag>
SearchNavByGroup(string group, int timeout=10000) -> IEnumerable<IRoomInfo>
    filter group:<group>

Common categories: "query", "hotel_view", "popular", "my_rooms", "my_fav", "my_history", "my_groups", "my_friends_rooms", "official"

IRoomInfo: Id, Name, OwnerId, OwnerName, Access, IsOpen/IsDoorbell/IsLocked/IsInvisible, Users, MaxUsers, Description, Trading, Score, Ranking, Category, Tags, IsGroupRoom, HasEvent, GroupId/GroupName/GroupBadge, EventName/EventDescription/EventMinutesRemaining

```csharp
var room = SearchNavByOwner("Sulake").FirstOrDefault() ?? throw Error("not found");
EnsureEnterRoom(room.Id);
Move(5, 5);
Delay(2000);
Turn(Directions.North);
```

Gotchas:
- No WalkTo that awaits arrival exists. To detect arrival, poll Self.XY / a user's position, or subscribe to OnEntityUpdated/OnEntitySlide.
- Move(x,y)/LookTo/Turn are client-agnostic; the task layer handles Flash/Unity wire differences.

### Entities & Users

state: Entities:IEnumerable<IEntity>, Users:IEnumerable<IRoomUser>, Pets:IEnumerable<IPet>, Bots:IEnumerable<IBot> (all empty when not in room)
state: Self:IRoomUser? (resolved via GetUserById(UserId); null until own entity arrives)

Lookups (all null if missing): GetEntityByIndex(int), GetEntity(string), GetEntityById(long), GetUser(int index), GetUser(string name), GetUserById(long), GetPet(int), GetPet(string), GetPetById(long), GetBot(int), GetBot(string), GetBotById(long)

Respect(long userId) -> void
Respect(IRoomUser) -> void
    sends Out.RespectUser
FriendRequest(IRoomUser) -> void
FriendRequest(string name) -> void
    sends Out.RequestFriend
Ignore(IRoomUser) -> void
Ignore(string name) -> void
    sends Out.IgnoreUser
Unignore(IRoomUser) -> void
Unignore(string name) -> void
    sends Out.UnignoreUser
Scratch(long petId) -> void
Scratch(IPet) -> void
    sends Out.RespectPet
Ride(long petId, bool mount) -> void
Ride(IPet, bool mount) -> void
    sends Out.MountPet
Mount(long) -> void
Mount(IPet) -> void
    sends Out.MountPet (shorthand)
Dismount(long) -> void
Dismount(IPet) -> void
    sends Out.MountPet (shorthand)

IEntity (all entity types): Id:long(persistent), Index:int(ephemeral room slot), Name:string, Motto:string, Figure:string, Type:EntityType, Location:Tile, X:int, Y:int, Z:float, XY:Point, Direction:int(0-7), Area, Dance:int(0=none), IsIdle:bool, IsTyping:bool, HandItem:int(0=none), Effect:int(0=none), IsRemoved:bool, IsHidden:bool, CurrentUpdate:IEntityStatusUpdate?, PreviousUpdate:IEntityStatusUpdate?

EntityType: User=1, Pet=2, PublicBot=3, PrivateBot=4

IEntityStatusUpdate: Index:int, Location:Tile, HeadDirection:int, Direction:int, Status:string(raw "/sit 0.5 0/mv 3,4,0.0/"), Stance:Stances, IsController:bool, ControlLevel:int, IsTrading:bool, MovingTo:Tile?(null if standing), SittingOnFloor:bool, ActionHeight:double?, Sign:Signs
    also implements IReadOnlyDictionary<string, IReadOnlyList<string>> for raw fragment access (update["sit"], update["mv"], case-insensitive)
Stances: Stand=0, Sit=1, Lay=2

IRoomUser adds: Gender:Gender, GroupId:long, GroupStatus, GroupName:string, FigureExtra:string, AchievementScore:int, IsModerator:bool, RightsLevel:int, HasRights:bool(RightsLevel>0)
    RightsLevel/HasRights are 0/false until the first status update

IPet adds: Breed, OwnerId:long, OwnerName:string, RarityLevel:int, HasSaddle:bool, IsRiding:bool, CanBreed:bool, CanHarvest:bool, CanRevive:bool, HasBreedingPermission:bool, Level:int, Posture

IBot adds: Gender, OwnerId:long, OwnerName:string, Data:IReadOnlyList<short>(private only)

Own profile (G.User) - instant cached props (throw if not loaded):
    UserData:IUserData, UserId:long, UserName:string, UserGender:Gender, UserFigure:string, UserMotto:string, UserNameChangeable:bool, UserAchievements, UserCredits:int, UserPoints:ActivityPoints, UserDiamonds:int(UserPoints[Diamond]), UserDuckets:int(UserPoints[Ducket]; silently 10 if missing)
    UserData extras: RealName, DirectMail, TotalRespects, RespectsLeft, ScratchesLeft, IsSafetyLocked, LastAccessDate, StreamPublishingAllowed

ActivityPointType: Ducket=0, Seashell=1, Heart=2, GiftPoint=3, Shell=4, Diamond=5
Gender: Male=0x01, Female=0x02, Unisex=Male|Female (flags)

SetUserMotto(string) -> void
    sends Out.ChangeAvatarMotto
SetUserFigure(string figure, Gender gender) -> void
SetUserFigure(string figure) -> void
    single-arg infers gender via Figure.Parse; throws if Unisex - use 2-arg overload when gender is known
GetUserBadges(int timeout=10000) -> List<Badge>
    Badge: Id, Code
GetUserGroups(int timeout=10000) -> List<GroupInfo>
    GroupInfo: Id, Name, BadgeCode, PrimaryColor, SecondaryColor, IsFavorite, OwnerId, HasForum
GetUserAchievements(int timeout=10000) -> IAchievements
    network fetch (distinct from cached prop)
GetUserRooms(int timeout=10000) -> IEnumerable<IRoomInfo>
    SearchNav("my","")

```csharp
foreach (var u in Users.Where(u => u.Id != UserId)) { Respect(u); Delay(500); }

Log($"{UserName} ({UserGender}) Credits:{UserCredits} Diamonds:{UserDiamonds}");
var pet = Pets.FirstOrDefault(p => p.OwnerName == UserName && p.HasSaddle);
if (pet != null) Mount(pet);
```

Gotchas:
- Index is reused after leave/rejoin; never persist it across room changes, key off Id.
- Self is null until the own entity appears in the room entity list.
- GetUser(string) name match is case-sensitive (server-driven).
- UserDuckets == 10 is a fallback, not a confirmed balance (Shockwave).

### Furni & Items

state: Furni:IEnumerable<IFurni>, FloorItems:IEnumerable<IFloorItem>, WallItems:IEnumerable<IWallItem>

GetFloorItem(long id) -> IFloorItem? (null if missing)
GetWallItem(long id) -> IWallItem? (null if missing)

UseFurni(IFurni) -> void
ToggleFurni(IFurni, int state) -> void
    dispatches by Type; Use = state 0
UseFloorItem(long id) -> void
ToggleFloorItem(long id, int state) -> void
    sends Out.UseStuff
UseWallItem(long id) -> void
ToggleWallItem(long id, int state) -> void
    sends Out.UseWallItem
UseGate(long id) -> void
UseGate(IFloorItem) -> void
    sends Out.EnterOneWayDoor
Place(IInventoryItem, Point, int dir=0) -> void
Place(IInventoryItem, WallLocation) -> void
    validates Type; uses item.ItemId
PlaceFloorItem(long itemId, Point, int dir=0) -> void
    protocol-aware (Flash string / Unity ints)
PlaceWallItem(long itemId, WallLocation) -> void
PlaceWallItem(long itemId, string location) -> void
    protocol-aware
Move(IFloorItem, Point, int dir=0) -> void
Move(IWallItem, WallLocation) -> void
Move(IWallItem, string) -> void
    sends Out.MoveRoomItem / Out.MoveWallItem
MoveFloorItem(long id, Point, int dir=0) -> void
MoveWallItem(long id, WallLocation) -> void
MoveWallItem(long id, string) -> void
    raw by id
Pickup(IFurni) -> void
    dispatches floor (type 2) / wall (type 1)
PickupFloorItem(long id) -> void
PickupWallItem(long id) -> void
    sends Out.PickItemUpFromRoom
DeleteWallItem(IWallItem) -> void
DeleteWallItem(long id) -> void
    sends Out.RemoveItem (stickies/photos)
UpdateStackTile(IFloorItem, float height) -> void
UpdateStackTile(long id, float height) -> void
    sends Out.StackingHelperSetCaretHeight; height in tiles (1.0f = one tile)

ItemType: Floor='s', Wall='i', Badge='b', Effect='e', Bot='r'

IItem: Type:ItemType, Kind:int(sprite id), Id:long
IFurni: OwnerId:long, OwnerName:string, State:int(-1 if non-numeric), SecondsToExpiration:int, Usage:FurniUsage, IsHidden:bool
IFloorItem: X:int, Y:int, XY:Point, Z:double(stack height in tile units), Height:float, Direction:int, Extra:long(consumable stage 0/1/2 OR linked teleporter id; disambiguate via Kind), Data:IItemData, StaticClass:string(populated only when Kind<0), Location:Tile, Area
IWallItem: Location:WallLocation, WX:int, WY:int, LX:int, LY:int, Orientation:WallOrientation, Data:string (plain string NOT IItemData)

FurniUsage: None=0, Rights=1, Anyone=2

Point: X:int, Y:int; tuple (3,5) and Tile implicitly convert; +/- operators
WallLocation: WX, WY, LX, LY, Orientation; Parse(string)/TryParse, implicit from string, Offset(wx,wy,scale), Add(...), Flip(), Orient(...), WallLocation.Zero, ToString() -> ":w=WX,WY l=LX,LY o"

```csharp
var item = Inventory.First(i => i.Type == ItemType.Floor && FurniData.GetInfo(i).Identifier == "throne");
Place(item, (5, 7), dir: 2);
Delay(500);
var placed = FloorItems.First(f => f.Kind == item.Kind);
ToggleFloorItem(placed.Id, 1);
```

Gotchas:
- For placement use item.ItemId (inventory slot), never item.Id. Place(IInventoryItem,...) does this for you.
- PlaceFloorItem/PlaceWallItem branch on Client; Shockwave throws "Unknown client protocol.". Move/pickup are protocol-agnostic.
- FurniData["identifier"] indexer throws if missing; use TryGetInfo (matching is case-insensitive).

### Inventory

state: Inventory:IInventory? (null if never loaded; may be stale if IsInvalidated)
state: PetInventory:IPetInventory?

EnsureInventory(int timeout=30000) -> IInventory
    returns cached if valid, else requests; must be in a room or server never responds; blocks/throws on timeout
EnsurePetInventory(int timeout=30000) -> IPetInventory

IInventory : IEnumerable<IInventoryItem>: IsInvalidated:bool, GetItem(long id)->IInventoryItem?, TryGetItem(long id, out item)->bool

IInventoryItem : IItem: Id:long, ItemId:long(slot id - use for trade/offer/place), Type:ItemType, Kind:int, Category:FurniCategory, Data:IItemData, IsTradeable:bool, IsRecyclable:bool, IsGroupable:bool, IsSellable:bool, SecondsToExpiration:int, HasRentPeriodStarted:bool, RoomId:long(non-zero if placed), SlotId:string, Extra:long

FurniCategory: Unknown=0, Normal=1, Wallpaper=2, Floor=3, Landscape=4, Sticky=5, Poster=6, Trax=7, Disk=8, Gift=9, MysteryBox=10, Trophy=11, GroupFurni=17, Clothing=23

IPetInventory : IEnumerable<IInventoryPet>: IsInvalidated, GetItem/TryGetItem
IInventoryPet: Id, Name, TypeId, PaletteId, Color, BreedId, CustomParts:List<int[]>, Level

Events (Action-only, no async overload): OnInventoryItemAdded(Action<InventoryItemEventArgs>), OnInventoryItemUpdated(Action<InventoryItemEventArgs>), OnInventoryItemRemoved(Action<InventoryItemEventArgs>)

```csharp
var inv = EnsureInventory();
foreach (var x in inv.Where(i => i.IsTradeable && i.Type == ItemType.Floor))
    Log($"{x.Kind} id={x.ItemId}");
```

Gotchas:
- EnsureInventory requires being in a room. Re-call when IsInvalidated.
- ItemId vs Id: pass ItemId to offer/place; Id may silently send the wrong value on Flash.

### Catalog & Marketplace

All catalog/marketplace calls are blocking, default timeout 10000 ms.

GetCatalog(string type="NORMAL", int timeout=10000) -> ICatalog
    full page tree
GetBcCatalog(int timeout=10000) -> ICatalog
    type="BUILDERS_CLUB"
GetCatalogPage(int pageId, string type="NORMAL", int timeout=10000) -> ICatalogPage
GetCatalogPage(ICatalogPageNode node, int timeout=10000) -> ICatalogPage
    node overload uses node.Catalog?.Type ?? "NORMAL"
GetBcCatalogPage(int pageId, int timeout=10000) -> ICatalogPage
    BC shorthand
Purchase(ICatalogOffer offer, int count=1, string extra="") -> void
Purchase(int pageId, int offerId, int count=1, string extra="") -> void
    requires offer.Page != null; fire-and-forget
    extra: trophy inscription text / group id as string for group furni / else ""
PurchaseAsGift(ICatalogOffer, string recipient, string message="", string extra="", string? giftFurni=null, GiftBox box=Basic, GiftDecor decor=None) -> void
    validates offer is gift-eligible; giftFurni defaults to random present_gen*

ICatalog: RootNode, Type, NewAdditionsAvailable, FindNode(title?, name?, id?), FindNode(Func<...,bool>), enumerable over nodes
ICatalogPageNode: Id(use with GetCatalogPage), Name, Title, IsVisible, Icon, OfferIds, Children, Catalog, Parent, FindNode(...), EnumerateDescendantsAndSelf()
ICatalogPage: Id, CatalogType, LayoutCode, Offers, Images, Texts, AcceptSeasonCurrencyAsCredits, Data
ICatalogOffer: Id, Page(required for Purchase), FurniLine, IsRentable, PriceInCredits, PriceInActivityPoints, ActivityPointType, CanPurchaseAsGift, CanPurchaseMultiple, Products, ClubLevel(0/1HC/2VIP), IsPet, PreviewImage
ICatalogProduct : IItem: Variant, Count, IsLimited, LimitedTotal, LimitedRemaining

GiftBox: Basic=-1, Royal=0, Imperial=1, Glamor=2, Cardboard=3, Steel=4, IceCube=5, Wooden=6, Valentines=8
GiftDecor: RedSilkKnotRibbon=0, None=10 (and values between)
GiftFurni constants: BasicRed="present_gen", BasicGray="present_gen6", WrapMaroon="present_wrap*1" through "present_wrap*10"

GetUserMarketplaceOffers(int timeout=10000) -> IUserMarketplaceOffers
    own listings; has CreditsWaiting
SearchMarketplace(string? searchText=null, int? from=null, int? to=null, MarketplaceSortOrder sort=HighestPrice, bool combineLtds=false, int timeout=10000) -> IEnumerable<IMarketplaceOffer>
    from/to = credit price range; combineLtds = merge LTD offers of the same item into one entry
GetMarketplaceInfo(ItemType type, int kind, int timeout=10000) -> IMarketplaceItemInfo
GetMarketplaceInfo(IItem, int timeout=10000) -> IMarketplaceItemInfo
GetMarketplaceInfo(FurniInfo, int timeout=10000) -> IMarketplaceItemInfo
    price history

MarketplaceSortOrder: HighestPrice=1, LowestPrice=2, MostTrades=3, LeastTrades=4, MostOffers=5, LeastOffers=6
IMarketplaceOffer : IItem: Id, Status:MarketplaceOfferStatus(Open=1, Sold=2, NotSold=3), Data, Price, TimeRemaining(min), Average, Offers
IMarketplaceItemInfo : IItem: Average(7-day), Offers(open), TradeInfo:IMarketplaceTradeInfo(DayOffset, AverageSalePrice, TradeVolume)

```csharp
var node = GetCatalog().FindNode(title: "Rare Furniture");
var page = GetCatalogPage(node);
var offer = page.Offers.First(o => o.Products.Any(p => FurniData.GetInfo(p).Identifier == "rare_dragonlamp_sd"));
Purchase(offer);

foreach (var o in SearchMarketplace("dragon lamp", sort: MarketplaceSortOrder.LowestPrice).Take(5))
    Log($"Id={o.Id} {o.Price}c avg={o.Average}c {o.TimeRemaining}m");
```

Gotchas:
- Purchase/PurchaseAsGift need offer.Page != null; get offers via a GetCatalogPage result, not detached.
- Purchase itself is fire-and-forget (no confirmation wait); poll inventory afterward.

### Trade

state: IsTrading:bool, IsTrader:bool(you initiated), HasAcceptedTrade:bool, HasPartnerAcceptedTrade:bool, IsTradeWaitingConfirmation:bool, TradePartner:IRoomUser?, OwnTradeOffer:ITradeOffer?, PartnerTradeOffer:ITradeOffer?

ITradeOffer: UserId:int, Items:IReadOnlyList<ITradeItem>(extends IInventoryItem + CreationDay/Month/Year), FurniCount:int, CreditCount:int

Trade(IRoomUser) -> void
Trade(int userIndex) -> void
    send trade request
Offer(IInventoryItem) -> void
Offer(long itemId) -> void
Offer(IEnumerable<IInventoryItem>) -> void
Offer(IEnumerable<long>) -> void
    add items via Out.TradeAddItems; nulls filtered
CancelOffer(IInventoryItem) -> void
CancelOffer(long itemId) -> void
    remove from your offer
AcceptTrade() -> void
    stage 1 accept
ConfirmTrade() -> void
    stage 2 confirm (after both accepted)
CancelTrade() -> void
    abort via Out.TradeClose

Events (Action<T> + Func<T,Task>): OnTradeOpened(TradeStartEventArgs), OnTradeOpenFailed(TradeStartFailEventArgs), OnTradeUpdated(TradeOfferEventArgs), OnTradeAccepted(TradeAcceptEventArgs), OnTradeWaitingConfirm(EventArgs), OnTradeClosed(TradeStopEventArgs), OnTradeCompleted(TradeCompleteEventArgs)

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
- Two-stage flow: AcceptTrade() -> wait for IsTradeWaitingConfirmation -> ConfirmTrade(). ConfirmTrade() before both accept is a no-op.
- Any offer change after acceptance resets accepted state; re-accept.

### Chat & Texts

Talk(string message, int bubble=0) -> void
    sends Out.Chat; normal speech (sends trailing -1 internally)
Shout(string message, int bubble=0) -> void
    sends Out.Shout
Whisper(IRoomUser recipient, string message, int bubble=0) -> void
Whisper(string recipient, string message, int bubble=0) -> void
    prepends recipient name automatically; do not prepend it yourself
Chat(ChatType chatType, string message, int bubble=0) -> void
    low-level dispatcher

ChatType: Talk=0, Shout=1, Whisper=2
bubble 0 = hotel default

External texts (Texts dict; Get* returns string?/null, TryGet* returns bool + out):

GetBadgeName(string code) -> string?
TryGetBadgeName(string code, out string name) -> bool
    key: badge_name_{code}
GetBadgeDescription(string code) -> string?
TryGetBadgeDescription(string code, out string) -> bool
    key: badge_desc_{code}
GetEffectName(int id) -> string?
TryGetEffectName(int id, out string) -> bool
    key: fx_{id}
GetEffectDescription(int id) -> string?
TryGetEffectDescription(int id, out string) -> bool
    key: fx_{id}_desc
GetHandItemName(int id) -> string?
TryGetHandItemName(int id, out string) -> bool
    key: handitem{id}
GetHandItemIds(string name) -> IEnumerable<int>
    reverse scan over Texts; do not call in tight loops

```csharp
Talk("hello room");
Shout("HELLO EVERYONE", 2);
var u = Users.First(u => u.Name == "Ducks");
Whisper(u, "hey");
string? name = GetBadgeName("ADI");
```

Gotchas:
- Never prepend the recipient name to Whisper yourself; both overloads do it.
- Use GetBadgeName over TryGetBadgeName (the TryGet variant queries the raw code without the badge_name_ prefix - inconsistent).
- GetHandItemIds full-scans Texts; do not call it in tight loops.

### Effects & Actions

ActivateEffect(int effectId) -> void
    sends Out.ActivateAvatarEffect; inventory action - CONSUMES a non-permanent effect; use with care
EnableEffect(int effectId) -> void
    sends Out.UseAvatarEffect; equip/display an already-owned effect
DisableEffect() -> void
    sends Out.UseAvatarEffect with -1; removes current effect

Action(int) -> void
Action(Actions action) -> void
    sends Out.Expression
Wave() -> void
ThumbsUp() -> void
Idle() -> void
Unidle() -> void
    shortcuts for Actions.Wave/ThumbsUp/Idle/None
Sit() -> void
Sit(bool) -> void
Stand() -> void
    sends Out.Posture (1=sit, 0=stand)
Dance() -> void
Dance(int id) -> void
Dance(Dances dance) -> void
StopDancing() -> void
    sends Out.Dance; Dance() = id 1, id 0 = stop
Sign(int) -> void
Sign(Signs) -> void
    sends Out.ShowSign

Actions: None=0, Wave=1, Kiss=2, Laugh=3, Idle=5, Jump=6, ThumbsUp=7
Dances: None=0, Dance=1, PogoMogo=2, DuckFunk=3, TheRollie=4
Signs: None=-1, Zero=0, One=1, Two=2, Three=3, Four=4, Five=5, Six=6, Seven=7, Eight=8, Nine=9, Ten=10, Heart=11, Skull=12, Exclamation=13, SoccerBall=14, Smile=15, RedCard=16, YellowCard=17

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
- ActivateEffect (inventory) != EnableEffect (display). ActivateEffect decrements quantity on consumable effects; prefer EnableEffect to just switch the visible effect.
- Kiss/Laugh/Jump have no shortcut; use Action(Actions.Kiss) etc.
- Sit/Stand use Out.Posture, Sign uses Out.ShowSign; distinct from expressions.

### Friends, Groups & Moderation

state: Friends:IEnumerable<IFriend>

IFriend: Id:long, Name:string, Gender, IsOnline:bool, CanFollow:bool, FigureString:string, CategoryId:int, Motto:string, RealName:string, IsAcceptingOfflineMessages:bool, IsVipMember:bool, IsPocketHabboUser:bool, Relation(None/Heart/Smile/Bob)

IsFriend(long id) -> bool
IsFriend(string name) -> bool
IsFriend(IRoomUser) -> bool
AddFriend(string name) -> void
AddFriend(IRoomUser) -> void
    send friend request
RemoveFriend(long) -> void
RemoveFriend(IFriend) -> void
RemoveFriends(IEnumerable<long>) -> void
RemoveFriends(IEnumerable<IFriend>) -> void
AcceptFriendRequest(long) -> void
AcceptFriendRequests(IEnumerable<long>) -> void
DeclineFriendRequest(long) -> void
DeclineFriendRequests(IEnumerable<long>) -> void
DeclineAllFriendRequests() -> void
SendMessage(long userId, string message) -> void
SendMessage(IFriend, string message) -> void
    private message

JoinGroup(long groupId) -> void
LeaveGroup(long groupId) -> void
    Leave kicks self
SetGroupFavourite(long groupId) -> void
RemoveGroupFavourite(long groupId) -> void
GetGroup(long groupId, int timeout=10000) -> IGroupData
GetGroupMembers(long groupId, int page=0, string filter="", GroupMemberSearchType searchType=Members, int timeout=10000) -> IGroupMembers
    page is 0-based
AcceptGroupMember(long groupId, long userId) -> void
RejectGroupMember(long groupId, long userId) -> void
KickGroupMember(long groupId, long userId) -> void

GroupMemberSearchType: Members=0, Admins=1, Requests=2
IGroupData: Id, Name, Description, Badge, HomeRoomId, HomeRoomName, Type, IsGuild, MemberStatus, MemberCount, PendingRequests, IsFavourite, IsOwner, IsAdmin, OwnerName, CanDecorateHomeRoom, HasForum, Created
IGroupMembers : IReadOnlyList<IGroupMember>: GroupId, GroupName, HomeRoomId, BadgeCode, TotalEntries, PageIndex, PageSize, IsAllowedToManage, SearchType, Filter
IGroupMember: Id, Name, Figure, Type, Joined:DateTime

Moderation (need rights/admin; roomId defaults to current room via RequireRoom()):

Mute(long userId, int minutes, long? roomId=null) -> void
Mute(IRoomUser, int minutes) -> void
    timed mute
Kick(long userId) -> void
Kick(IRoomUser) -> void
Ban(long userId, BanDuration) -> void
Ban(IRoomUser, BanDuration) -> void
    current room
Unban(long userId, long? roomId=null) -> void
Unban(IRoomUser) -> void
GiveRights(long userId) -> void
GiveRights(IRoomUser) -> void
RemoveRights(IEnumerable<long>) -> void
RemoveRights(IEnumerable<IRoomUser>) -> void

BanDuration: Hour, Day, Permanent

```csharp
long gid = 12345L;
var first = GetGroupMembers(gid, 0);
int pages = (int)Math.Ceiling((double)first.TotalEntries / first.PageSize);
var all = first.ToList();
for (int p = 1; p < pages; p++) all.AddRange(GetGroupMembers(gid, p));
Log($"Loaded {all.Count} members");
```

Gotchas:
- GetGroupMembers returns one page; loop p until (p * PageSize) >= TotalEntries.
- Moderation throws InvalidOperationException when not in a room (default roomId).

### Stickies & Misc

PlaceSticky(IInventoryItem, WallLocation) -> void
    throws if Category != FurniCategory.Sticky
PlaceSticky(long itemId, WallLocation) -> void
    no category check
PlaceStickyWithPole(IInventoryItem, WallLocation, string color, string text) -> void
PlaceStickyWithPole(long itemId, WallLocation, string color, string text) -> void
    sends Out.AddSpamWallPostIt
GetSticky(IWallItem, int timeout=10000) -> Sticky
GetSticky(long itemId, int timeout=10000) -> Sticky
    blocking fetch
UpdateSticky(Sticky) -> void
UpdateSticky(IWallItem, string color, string text) -> void
UpdateSticky(long itemId, string color, string text) -> void
    save changes
DeleteSticky(Sticky) -> void
    delegates to DeleteWallItem(sticky.Id)

Sticky: Id:long(wall item id), Color:string(6-hex), Text:string, Colors:StickyColors(static)
StickyColors (implicit string): Blue="9CCEFF", Pink="FF9CFF", Green="9CFF9C", Yellow="FFFF33"

SearchUser(string name, int timeout=10000) -> UserSearchResult?
    exact case-insensitive match or null
SearchUsers(string name, int timeout=10000) -> UserSearchResults
    has .Friends and .Others; .GetResult(name)
GetProfile(long userId, int timeout=10000) -> IUserProfile
    numeric id required; throws on timeout

UserSearchResult: Id, Name, Motto, Figure, RealName, Online
IUserProfile: Id, Name, Figure, Motto, Created, ActivityPoints, Friends, IsFriend, IsFriendRequestSent, IsOnline, Groups, LastLogin:TimeSpan, Level, StarGems

Randomness (static, uses Random.Shared):
    Rand() -> int
    Rand(int max) -> int (max exclusive)
    Rand(int min, int max) -> int (upper bound exclusive)
    Rand(byte[]) -> byte
    RandDouble() -> double
    Rand<T>(IEnumerable<T>) -> T (returns default if empty)
    Rand<T>(T[]) -> T (throws if empty)

UI (scripter window only): SetDarkTheme(bool) -> void, SetBackgroundColor(byte r, byte g, byte b) -> void

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
- SearchUser resolves name -> id; combine with GetProfile(id) since GetProfile needs the numeric id.
- Rand<T>(IEnumerable<T>) returns default (possibly null) on empty; the array overload throws; guard before use.
- Rand/Rand(min,max) upper bound is exclusive.

## Packets, headers & interception

### Header sets: In, Out, Client

state: In:Incoming (server->client headers, Destination.Client), Out:Outgoing (client->server headers, Destination.Server), Client:ClientType (Flash or Unity, live session; Shockwave exists in enum but header system only resolves Flash/Unity slots)

- In/Out are Headers : Dictionary-style bags. Every known message is a typed Header property and reachable by indexer: Out.Chat == Out["Chat"] (case-insensitive). A miss on the indexer throws UnknownHeaderException; the typed property won't compile if it doesn't exist.
- A Header carries a Flash and a Unity ClientHeader? slot plus an optional raw Value. The framework resolves the right numeric wire value per Client internally - never hardcode numbers.
- Flash/Unity name divergence: the typed property name is always the Unity name. A message can have a different Flash name pointing at the same Header (both names land in the name map). Flash-only messages have a name-map entry but no typed property - reach them via In["FlashOnlyName"] / Out["FlashOnlyName"]. Guard with In.MessageExists("Name") / In.TryGetHeader("Name", out var h).
- Unresolved headers throw: before the connection is live (no Load), or when a name isn't bound for the current client, the internal GetValue(Client) throws UnresolvedHeaderException. Send/Receive by name only work on a live session.

### Raw / unmapped / negative headers

When the message manager has no name for what you need (debugging, Shockwave, custom servers), build a Header from a raw numeric value. Naming is from the script author's perspective:

```csharp
Header inHdr  = Header.In(4001);   // Destination.Client  (incoming, server->client)
Header outHdr = Header.Out(2401);  // Destination.Server  (outgoing, client->server)

Send(outHdr);                       // empty-body raw send
Send(Header.Out(2401), "hello", 0); // raw header + payload (see Send<T...> below)
var pkt = Receive(Header.In(4001), timeout: 5000);
```

- Raw headers bypass the name map entirely - only use as a last resort.
- "Negative headers" surface as Value <= 0 meaning unresolved - GetValue throws on <= 0. A raw Header.Out(-1) is not sendable. If you must encode a -1 field (e.g. "no target"), that is a packet field value, not a header - write it with WriteInt(-1) / WriteLegacyShort(-1).

### Sending

Send(IReadOnlyPacket packet) -> void
    Direction taken from packet.Header.Destination.
Send(Header header) -> void
    Zero-payload packet.
Send<T1..Tn>(Header header, T1 a1, ...) -> void
    Source-generated shorthand - builds the packet and type-dispatches each arg (see encoding table below).
SendAsync(IReadOnlyPacket) -> ValueTask  [async]
SendAsync(Header) -> ValueTask  [async]
SendAsync<T...>(Header, ...) -> ValueTask  [async]

Send<T...> / Write<T> / WriteObject arg type-dispatch (this is where Flash bites you):

    bool           -> WriteBool          Flash: 1 byte          Unity: 1 byte
    byte           -> WriteByte          Flash: 1 byte          Unity: 1 byte
    short/ushort   -> WriteShort         Flash: 2 BE            Unity: 2 BE
    int/uint       -> WriteInt           Flash: 4 BE            Unity: 4 BE
    long/ulong     -> WriteLegacyLong    Flash: 4 bytes (truncated!)  Unity: 8 bytes
    float          -> WriteFloat         Flash: 4-byte float (wrong wire shape!)  Unity: 4-byte float
    string         -> WriteString        Flash: u16-len + UTF-8 Unity: same
    LegacyShort    -> WriteLegacyShort   Flash: int (4)         Unity: short (2)
    LegacyFloat    -> WriteLegacyFloat   Flash: float-as-string Unity: 4-byte float
    LegacyLong     -> WriteLegacyLong    Flash: int (4)         Unity: long (8)
    IComposable    -> Write(IComposable) composes self
    ICollection/IEnumerable -> count via WriteLegacyShort then each item; Flash count: int, Unity count: short

Trap: passing a C# float literal sends a raw 4-byte float, which Flash does not expect (Flash floats are length-prefixed ASCII). For any float in a cross-client packet, pass a LegacyFloat, not a float. A C# long becomes a legacy long (truncates on Flash); pass int if the value really is 32-bit.

IPacket write methods (all fluent -> IPacket; each has a (value, int position) overload that writes at an absolute offset without advancing):

    WriteBool, WriteByte, WriteShort, WriteInt, WriteFloat, WriteLong*, WriteString,
    WriteLegacyShort, WriteLegacyFloat, WriteLegacyLong, WriteFloatAsString,
    WriteBytes(ReadOnlySpan<byte>), Write(IComposable), Replace(params object[]),
    ReplaceString(string), ModifyString(Func<string,string>)

*WriteLong throws when Protocol == Flash - use WriteLegacyLong.

```csharp
Send(Out.GetCredits);                       // header only
Send(Out.Chat, "hello room", 0, 0);         // shorthand: string + int + int
await SendAsync(Out.GetCredits);

var p = new Packet(Out.Chat, Client);       // manual build (fluent, returns IPacket)
p.WriteString("hello").WriteInt(0).WriteShort(0);
Send(p);
```

### Receiving / one-shot capture

Receive(HeaderSet, int timeout=-1, bool block=false) -> IReadOnlyPacket
    Sync, blocks. timeout=-1 waits forever. block=true drops the packet (never reaches destination).
    Returned packet is a copy (e.Packet.Copy()), safe to read after the call.
Receive(ITuple, int timeout=-1, bool block=false) -> IReadOnlyPacket
    ex: Receive((In.Chat, In.Shout), timeout: 5000)
ReceiveAsync(HeaderSet/ITuple, int timeout=-1, bool block=false) -> Task<IPacket>  [async]
TryReceive(HeaderSet, out IReadOnlyPacket? packet, int timeout=-1, bool block=false) -> bool
    Caveat: its catch only handles OperationCanceledException when (!Ct.IsCancellationRequested).
    A plain timeout surfaces as TimeoutException, which TryReceive does NOT swallow - it propagates.
    To treat timeout as "no packet", wrap Receive yourself (see example).

- Timeout throws TimeoutException (the internal cancel is rethrown as TimeoutException by the interceptor task), not OperationCanceledException. Script cancel (Ct) propagates as OperationCanceledException.

```csharp
IReadOnlyPacket? chat = null;
try { chat = Receive((In.Chat, In.Shout), timeout: 5000); }
catch (TimeoutException) { Log("no chat in 5s"); }

Send(Out.GetCredits);                        // request/response idiom
int credits = Receive(In.Credits, timeout: 5000).ReadInt();

var room = await ReceiveAsync(In.RoomReady, timeout: 3000, block: true); // capture + suppress
```

IReadOnlyPacket read methods (each advances Position; most have a (int pos) overload that reads at an absolute offset):

    ReadBool()          Flash: 1 byte (throws if not 0/1)       Unity: same
    ReadByte()          Flash: 1 byte                           Unity: same
    ReadShort()         Flash: 2 BE                             Unity: same
    ReadInt()           Flash: 4 BE                             Unity: same
    ReadFloat()         Flash: 4-byte float (Unity only in practice)  Unity: 4-byte float
    ReadLong()          Flash: throws                           Unity: 8 BE
    ReadString()        Flash: u16-len + UTF-8                  Unity: same
    ReadFloatAsString() Flash: parse float from string          Unity: same
    ReadLegacyShort()   Flash: reads int (4)                    Unity: reads short (2)
    ReadLegacyFloat()   Flash: string -> float                  Unity: 4-byte float
    ReadLegacyLong()    Flash: reads int (4) -> long            Unity: reads long (8)

Navigation: Position (get/set; throws if <0 or >Length), Available (Length-Position), Length, Skip(int bytes), Skip(params Type[]) (uses Packet.Bool/Byte/Short/Int/Float/Long/String; protocol-aware for long/float), CanReadBool(), CanReadString().

Generic Read<T> extensions (not all legacy-aware):

    Read<bool/byte/short/int/string>()      -> raw ReadBool/Byte/Short/Int/String
    Read<long>()                            -> ReadLegacyLong (legacy)
    Read<float>()                           -> ReadFloat (raw, NOT legacy!)
    Read<LegacyShort/LegacyFloat/LegacyLong>() -> the matching legacy method
    ReadList<T>()                           -> count via ReadLegacyShort, then N x Read<T>()

pkt.Read<int>() reads a raw 4-byte int on both clients (it is NOT a legacy short). For a count/index field that is 4 bytes on Flash but 2 on Unity, use ReadLegacyShort() or Read<LegacyShort>(). For a float in a cross-client packet, use ReadLegacyFloat() / Read<LegacyFloat>(), never Read<float>().

### Intercepting (persistent, live callbacks)

OnIntercept(Header, Action<InterceptArgs>) -> void
    Single header.
OnIntercept(ITuple, Action<InterceptArgs>) -> void
    ex: OnIntercept((In.Chat, In.Shout, In.Whisper), e => ...)
OnIntercept(HeaderSet, Action<InterceptArgs>) -> void
    Set form.
OnIntercept(Header/ITuple/HeaderSet, Func<InterceptArgs, Task>) -> void
    Async callback - fire-and-forget (callback(e) is not awaited).

- Use OnIntercept for callbacks that live the whole script; use Receive* for one-shot. All registrations are tracked and auto-removed when the script ends - no manual cleanup.
- Async-callback trap: the Func<...,Task> overload wraps to e => { callback(e); } - the task is never awaited, so exceptions inside (including bad packet reads) are silently swallowed, and blocking decisions made after an await are too late. Keep e.Block() and packet reads synchronous; only await after you have already decided.
- The dispatcher sets e.Packet.Position = 0 before each callback - you always read from the start.
- Registering the same delegate instance against the same header twice throws InvalidOperationException; distinct lambdas are fine.

InterceptArgs (do not retain past the callback - disposed by the framework afterward):

    Packet: IPacket - live, readable and writable; assign a new IPacket to replace it wholesale
    OriginalPacket: IReadOnlyPacket - frozen snapshot before any edit
    Destination: Destination - Client (incoming) / Server (outgoing)
    IsIncoming, IsOutgoing: bool
    Step: int - sequence number of the packet stream
    Timestamp: DateTime - intercept time
    IsBlocked, IsModified: bool - blocked state; whether Packet differs from OriginalPacket
    Block() -> void - drop the packet; idempotent, cannot be undone

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

    ReadLegacyShort / WriteLegacyShort   Flash: int (4 bytes)                  Unity: short (2 bytes)
    ReadLegacyLong / WriteLegacyLong     Flash: int (4 bytes; write truncates)  Unity: long (8 bytes)
    ReadLegacyFloat / WriteLegacyFloat   Flash: float as ASCII string field      Unity: 4-byte float
    ReadLong / WriteLong (raw)           Flash: throws                           Unity: OK
    raw ReadFloat / WriteFloat           Flash: 4-byte float = wrong wire shape  Unity: OK
    list / collection count prefix       Flash: int (via LegacyShort)            Unity: short
    header numeric resolution            Flash: Flash slot                        Unity: Unity slot
    typed property name                  Flash: resolves via name map (Flash name may differ)  Unity: property == Unity name

Rules of thumb:
- For any field whose size differs by client (counts, item/user IDs, floats), use the Legacy read/write (or LegacyShort/LegacyFloat/LegacyLong wrapper types with Read<T>/Send<T...>). Plain int/long/float are raw fixed-width and silently wrong cross-client.
- Send/Receive/OnIntercept by In.*/Out.* are client-agnostic - the framework resolves the correct numeric header. Only drop to Header.In(n)/Header.Out(n) when no name exists.
- Branch on Client == ClientType.Flash / ClientType.Unity only when raw-field layout genuinely diverges and the Legacy helpers don't cover it.

## Events

On* methods register persistent callbacks for the lifetime of the script. Every On* has two overloads - Action<TEventArgs> (sync) and Func<TEventArgs, Task> (async) - except the three inventory events, which are Action<T>-only. All subscriptions are auto-removed when the script ends (Unsubscriber cleanup), so you never manually unsubscribe in normal scripts.

For one-shot captures use Receive/ReceiveAsync instead; reserve On* for whole-script-lifetime handlers.

### Room

    OnEnteredQueue(Action<EventArgs> | Func<EventArgs,Task>) -> void
        joined room entry queue
    OnQueueUpdate(Action<EventArgs> | Func<EventArgs,Task>) -> void
        queue position changed
    OnEnteringRoom(Action<EventArgs> | Func<EventArgs,Task>) -> void
        began entering room
    OnEnteredRoom(Action<RoomEventArgs> | Func<RoomEventArgs,Task>) -> void
        fully entered room
    OnLeftRoom(Action<EventArgs> | Func<EventArgs,Task>) -> void
        left room
    OnKicked(Action<EventArgs> | Func<EventArgs,Task>) -> void
        kicked from room
    OnRoomDataUpdate(Action<RoomDataEventArgs> | Func<RoomDataEventArgs,Task>) -> void
        room metadata updated

### Floor items

    OnFloorItemsLoaded(Action<FloorItemsEventArgs> | Func<FloorItemsEventArgs,Task>) -> void
        initial floor-item load on entry
    OnFloorItemAdded(Action<FloorItemEventArgs> | Func<FloorItemEventArgs,Task>) -> void
        floor item placed
    OnFloorItemUpdated(Action<FloorItemUpdatedEventArgs> | Func<FloorItemUpdatedEventArgs,Task>) -> void
        floor item moved/rotated
    OnFloorItemDataUpdated(Action<FloorItemDataUpdatedEventArgs> | Func<FloorItemDataUpdatedEventArgs,Task>) -> void
        state change (gate open, animation, etc.)
    OnFloorItemSlide(Action<FloorItemSlideEventArgs> | Func<FloorItemSlideEventArgs,Task>) -> void
        slid via roller/wired
    OnFloorItemRemoved(Action<FloorItemEventArgs> | Func<FloorItemEventArgs,Task>) -> void
        floor item removed

### Wall items

    OnWallItemsLoaded(Action<WallItemsEventArgs> | Func<WallItemsEventArgs,Task>) -> void
        initial wall-item load
    OnWallItemAdded(Action<WallItemEventArgs> | Func<WallItemEventArgs,Task>) -> void
        wall item placed
    OnWallItemUpdated(Action<WallItemUpdatedEventArgs> | Func<WallItemUpdatedEventArgs,Task>) -> void
        wall item updated
    OnWallItemRemoved(Action<WallItemEventArgs> | Func<WallItemEventArgs,Task>) -> void
        wall item removed

### Inventory

Action<T>-only - no async overload for any of these three.

    OnInventoryItemAdded(Action<InventoryItemEventArgs>) -> void
        item added to inventory
    OnInventoryItemUpdated(Action<InventoryItemEventArgs>) -> void
        inventory item updated
    OnInventoryItemRemoved(Action<InventoryItemEventArgs>) -> void
        item removed from inventory

### Entities

    OnEntityAdded(Action<EntityEventArgs> | Func<EntityEventArgs,Task>) -> void
        one entity entered room
    OnEntitiesAdded(Action<EntitiesEventArgs> | Func<EntitiesEventArgs,Task>) -> void
        batch of entities loaded
    OnEntityUpdated(Action<EntityEventArgs> | Func<EntityEventArgs,Task>) -> void
        entity position/state update
    OnEntitySlide(Action<EntitySlideEventArgs> | Func<EntitySlideEventArgs,Task>) -> void
        entity slid on roller
    OnUserDataUpdated(Action<EntityDataUpdatedEventArgs> | Func<EntityDataUpdatedEventArgs,Task>) -> void
        figure/gender/motto/achievement-score changed
    OnEntityIdle(Action<EntityIdleEventArgs> | Func<EntityIdleEventArgs,Task>) -> void
        idle status toggled
    OnEntityDance(Action<EntityDanceEventArgs> | Func<EntityDanceEventArgs,Task>) -> void
        dance changed
    OnEntityHandItem(Action<EntityHandItemEventArgs> | Func<EntityHandItemEventArgs,Task>) -> void
        hand item changed
    OnEntityEffect(Action<EntityEffectEventArgs> | Func<EntityEffectEventArgs,Task>) -> void
        effect changed
    OnEntityAction(Action<EntityActionEventArgs> | Func<EntityActionEventArgs,Task>) -> void
        entity performed an action
    OnEntityRemoved(Action<EntityEventArgs> | Func<EntityEventArgs,Task>) -> void
        entity left room

### Chat

    OnChat(Action<EntityChatEventArgs> | Func<EntityChatEventArgs,Task>) -> void
        any entity chats (normal/shout/whisper)

### Bot loop shape

Register all On* handlers up front, then call Wait() (blocks until cancel/Finish()) to keep the script alive so callbacks keep firing. Use Finish() inside any callback to stop the loop. Do not spin a manual while (Run) loop just to keep events alive - Wait() is the keep-alive.

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

Async handlers work identically - pass a Func<TEventArgs, Task>:

```csharp
OnEnteredRoom(async e => {
    Send(Out.GetRoomData);
    var data = await ReceiveAsync(In.RoomData, timeout: 3000);
    Log($"Floor: {data.ReadString()}");
});
Wait();
```

## Data models reference

Objects below are returned live by G (the globals class). All are interfaces; concrete classes (FloorItem, RoomUser, etc.) implement them. Live state mutates as packets arrive; never cache Index across rooms.

### IRoom + RoomData/Info

G.Room is IRoom? - null while loading; gate on G.IsInRoom first.

    state: Id:long, Data:IRoomData, Name:string, Description:string, OwnerId:long, OwnerName:string
    state: Access:RoomAccess, MaxUsers:int, Trading:TradePermissions, Score:int, Ranking:int
    state: Category:RoomCategory, Tags:IReadOnlyList<string>, Flags:RoomFlags
    state: IsOpen, IsDoorbell, IsLocked, IsInvisible (derived from Access)
    state: HasEvent, IsGroupRoom, AllowPets (derived from Flags)
    state: GroupId:long, GroupName:string, GroupBadge:string (valid only when IsGroupRoom)
    state: EventName:string, EventDescription:string, EventMinutesRemaining:int (valid only when HasEvent)
    state: Model:string, Floor:string?, Wallpaper:string?, Landscape:string? (null until loaded)
    state: DoorTile:Tile, EntryDirection:int, FloorPlan:IFloorPlan, Heightmap:IHeightmap
    state: HideWalls:bool, WallThickness:Thickness, FloorThickness:Thickness
    state: Moderation:IModerationSettings, ChatSettings:IChatSettings

RoomAccess: Open=0, Doorbell=1, Password=2, Invisible=3, Friends=7
TradePermissions: NotAllowed=0, RightsHolders=1, Allowed=2
RoomCategory: Party=2, Games=3, FansiteSquare=5, HelpCenters=6, PersonalSpace=10, BuildingAndDecoration=11, ChatAndDiscussion=12, Trading=14, Agencies=16, RolePlaying=17
RoomFlags: [Flags] HasOfficialRoomPic=1, IsGroupHomeRoom=2, HasEvent=4, ShowOwnerName=8, AllowPets=16, ShowRoomAd=32
Thickness: Thinnest=-2, Thin=-1, Normal=0, Thick=1

Collections (all lazy IEnumerable): Furni (floor+wall), FloorItems, WallItems, Entities, Users, Pets, Bots.

Lookups:
    GetFloorItem(long) -> IFloorItem?
    GetWallItem(long) -> IWallItem?
    GetFurni(ItemType, long) -> IFurni?
    HasFloorItem(long) -> bool
    HasWallItem(long) -> bool
    GetEntity<T>(int index | string name) -> T?
    GetEntityById<T>(long) -> T?
    TryGet… variants (non-throwing)

IRoomData : IRoomInfo adds: IsEntering:bool (true only on first load of this entry), Forward, IsGroupMember, IsRoomMuted, Moderation, CanMute, ChatSettings. Fetch without entering via GetRoomData(roomId).

RoomInfo (navigator entry, from SearchNav/GetNav) carries the same identity/access/group/event fields plus Users/MaxUsers counts and OfficialRoomPicRef.

### IEntity / IRoomUser (+ status)

IEntity is the base for every live entity (users, pets, bots).

    state: Index:int (ephemeral per-room slot - used by packets/status updates, never persist), Id:long (persistent)
    state: Name:string, Motto:string, Figure:string
    state: Location:Tile, X:int, Y:int, XY:Point, Z:float (height in tiles; ~0.5 = sitting)
    state: Direction:int (body facing 0-7; raw int, not the Directions enum)
    state: Dance:int (0=none; cast to Dances enum), IsIdle:bool, IsTyping:bool
    state: HandItem:int (held drink/prop type ID; 0=none; not an inventory ID)
    state: Effect:int (avatar effect ID; 0=none)
    state: CurrentUpdate:IEntityStatusUpdate? (null before first update)
    state: PreviousUpdate:IEntityStatusUpdate? (null before first update)
    state: IsRemoved:bool, IsHidden:bool (stale-ref guard / client-side hide)
    state: Type:EntityType

EntityType: User=1, Pet=2, PublicBot=3, PrivateBot=4
Dances: None=0, Dance=1, PogoMogo=2, DuckFunk=3, TheRollie=4

IRoomUser : IEntity adds:
    state: Gender ([Flags] Male=1, Female=2, Unisex=Male|Female)
    state: GroupId:long (-1 if none), GroupStatus, GroupName:string
    state: FigureExtra:string, AchievementScore:int, IsModerator:bool
    state: RightsLevel:int (from CurrentUpdate.ControlLevel; 0=none; 0/false until first status update)
    state: HasRights:bool (RightsLevel > 0)
    state: BadgeRank (Flash only)

IEntityStatusUpdate (CurrentUpdate/PreviousUpdate) - per-tick movement/state:
    state: Index:int (matches entity), Location:Tile (tile after this tick)
    state: HeadDirection:int, Direction:int (head can differ from body)
    state: Status:string (raw "/sit 0.5 0/mv 3,4,0.0/")
    state: Stance:Stances, IsController:bool, ControlLevel:int (rights / flatctrl)
    state: IsTrading:bool (trd fragment)
    state: MovingTo:Tile? (destination this tick; null if stationary)
    state: SittingOnFloor:bool, ActionHeight:double? (Z offset for sit/lay; null standing)
    state: Sign:Signs (shown sign or None=-1)

Stances: Stand=0, Sit=1, Lay=2
Signs: None=-1, Zero=0, One=1, Two=2, Three=3, Four=4, Five=5, Six=6, Seven=7, Eight=8, Nine=9, Ten=10, Heart=11, Skull=12, Exclamation=13, SoccerBall=14, Smile=15, RedCard=16, YellowCard=17

IEntityStatusUpdate also implements IReadOnlyDictionary<string, IReadOnlyList<string>> for raw fragment access (update["sit"], update["mv"], case-insensitive).

IBot : IEntity adds: Gender, OwnerId:long, OwnerName:string (-1/empty for public bots), Data:IReadOnlyList<short> (skills; private bots only), IsPublicBot:bool, IsPrivateBot:bool.

IPet : IEntity adds: Breed, OwnerId:long, OwnerName:string, RarityLevel:int, HasSaddle:bool, IsRiding:bool, CanBreed:bool, CanHarvest:bool, CanRevive:bool, HasBreedingPermission:bool, Level:int, Posture.

### IFurni / IFloorItem / IWallItem

IItem base of all items:
    state: Type:ItemType, Kind:int (furni type), Id:long (instance ID)

ItemType: Floor='s', Wall='i', Badge='b', Effect='e', Bot='r'

IFurni : IItem (floor + wall room items):
    state: OwnerId:long, OwnerName:string
    state: State:int (-1 if unparseable)
    state: SecondsToExpiration:int (-1=never)
    state: Usage:FurniUsage, IsHidden:bool (client-side only)

FurniUsage: None=0, Rights=1, Anyone=2

IFloorItem : IFurni:
    state: X:int, Y:int (tile col/row), XY:Point, Z:double (stack height in tile units)
    state: Height:float (item's own visual height)
    state: Direction:int (0=N, 2=E, 4=S, 6=W; diagonals odd)
    state: Extra:long (overloaded: consumable stage 0/1/2 OR linked teleporter ID - disambiguate via Kind)
    state: Data:IItemData, StaticClass:string (populated only when Kind < 0)
    state: Location:Tile, Area:Area

FloorItem.State derives from Data.Value via double.TryParse (handles "1.0").

IWallItem : IFurni:
    state: Location:WallLocation, WX:int, WY:int (wall segment coords)
    state: LX:int, LY:int (local offset in segment)
    state: Orientation:WallOrientation (Left='l', Right='r')
    state: Data:string (plain string, NOT IItemData)

WallItem.State = int.TryParse(Data) directly; -1 if non-integer. Never cast IWallItem.Data to IItemData.

### IInventoryItem

IInventoryItem : IItem - items in your inventory.

    state: ItemId:long (inventory-slot ID - use for GetItem, trade Offer(itemId), add/remove events)
    state: Id:long (in-room furni instance ID - different value from ItemId)
    state: Category:FurniCategory
    state: Data:IItemData, IsTradeable:bool, IsRecyclable:bool, IsGroupable:bool, IsSellable:bool
    state: SecondsToExpiration:int (-1 or 0 = permanent)
    state: HasRentPeriodStarted:bool
    state: RoomId:long (0 if in inventory; non-zero if placed)
    state: SlotId:string (floor items only)
    state: Extra:long (same overloaded field as IFloorItem.Extra; Flash truncates to 32-bit)

FurniCategory: Unknown=0, Normal=1, Wallpaper=2, Floor=3, Landscape=4, Sticky=5, Poster=6, Trax=7, Disk=8, Gift=9, MysteryBox=10, Trophy=11, GroupFurni=17, Clothing=23 (plus horse/plant variants)

Load via EnsureInventory() (blocking, must be in a room). G.Inventory is null until first load and may be IsInvalidated.

### IItemData (floor + inventory data)

Floor and inventory Data is IItemData. Wall items are NOT.

    state: Type:ItemDataType, Flags:ItemDataFlags (only IsLimitedRare=1), IsLimitedRare:bool
    state: UniqueSerialNumber:int, UniqueSeriesSize:int (meaningful only when IsLimitedRare)
    state: Value:string (legacy string repr), State:int (int.TryParse(Value); -1 if non-integer)

ItemDataType discriminator - pattern-match with Data is ...:

    Legacy=0        -> ILegacyData                               single string in Value (most furni)
    Map=1           -> IMapData : IReadOnlyDictionary<string,string>  key/value (e.g. "state")
    StringArray=2   -> IStringArrayData : IReadOnlyList<string>  indexed strings
    VoteResult=3    -> IVoteResultData                           + Result:int
    Empty=4         -> IEmptyItemData                            no data
    IntArray=5      -> IIntArrayData : IReadOnlyList<int>        indexed ints
    HighScore=6     -> IHighScoreData : IReadOnlyList<IHighScore> + ScoreType:int, ClearType:int;
                       each IHighScore: Value:int, Names:IReadOnlyList<string>
    CrackableFurni=7 -> ICrackableFurniData                     + Hits:int, Target:int

```csharp
var item = Room.FloorItems.First(x => x.Kind == 1234);
if (item.Data.IsLimitedRare)
    Log($"LTD #{item.Data.UniqueSerialNumber}/{item.Data.UniqueSeriesSize}");
if (item.Data is IMapData map && map.TryGetValue("state", out var v)) Log(v);
if (item.Data is ICrackableFurniData c) Log($"{c.Hits}/{c.Target}");
```

### Heightmap + HeightmapTile

G.Heightmap (IHeightmap?) - live stacking heights updated as furni moves. Distinct from FloorPlan (static model heights).

IHeightmap:
    state: Width:int, Length:int
    this[int x, int y] -> IHeightmapTile (throws OOB)
    this[(int X, int Y)] -> IHeightmapTile (throws OOB)
    IEnumerable<IHeightmapTile> (foreach-able)

IHeightmapTile:
    state: X:int, Y:int, Location:(int X, int Y)
    state: IsFloor:bool (true = floor tile, not void)
    state: IsBlocked:bool (a solid furni occupies it - NOT "a user stands here")
    state: IsFree:bool (IsFloor && !IsBlocked - safe to place furni)
    state: Height:double (stack height in Habbo units; (value & 0x3FFF)/256.0; -1 if not a floor tile)

ex: var t = Heightmap?[DoorTile.X, DoorTile.Y]; bool canStack = t?.IsFree ?? false;

IFloorPlan (static model, does not change with furni):
    state: Width:int, Length:int, Scale:int (64=normal, 32=legacy), WallHeight:int (-1=default)
    state: OriginalString:string?
    this[int x, int y] -> int height (-1 = void)
    IsWalkable(x, y) -> bool (height >= 0; also tuple/Tile overloads)

Height char map: 0-9 -> 0-9, a-w -> 10-32, x/X -> void.

### Tile / Point + directions

Tile (readonly struct):
    state: X:int, Y:int, Z:float (altitude), XY:Point
    ctors: (x,y) with Z=0; (x,y,z)
    implicit from (int,int,float) and (int,int,double) tuples
    Parse(string) / TryParse for "(x,y,z)"
    + / - with Point (shifts X/Y, keeps Z) or Tile (all three)
    == / != with Point ignores Z
    Equals(Tile, float epsilon) for Z comparison (Z serialized as string; exact float equality unreliable)

Point (readonly struct):
    state: X:int, Y:int
    + / -, == / !=
    implicit from (int,int) and from Tile (drops Z)

Directions: North=0, NorthEast=1, East=2, SouthEast=3, South=4, SouthWest=5, West=6, NorthWest=7
    entity/item Direction is a raw int - cast to Directions for readability

Area (axis-aligned rect, IEnumerable<Point>):
    state: X1:int, Y1:int, Width:int, Length:int
    derived: X2:int, Y2:int, Origin:Point, Opposite:Point, Size
    Contains(Point) / Intersects(Area) / Flip()
    implicit from (int,int,int,int) = x1,y1,x2,y2

WallLocation:
    state: WX:int, WY:int, LX:int, LY:int, Orientation:WallOrientation
    Parse(string) / TryParse, Flip(), Offset(wx,wy,scale), Add(...), Orient(...)
    WallLocation.Zero, ToString() -> ":w=WX,WY l=LX,LY o"
    implicit from string

WallOrientation: Left='l', Right='r'; IsLeft:bool, IsRight:bool; implicit to/from char

### FurniData & ExternalTexts (identifier <-> kind <-> name)

G.GameData.Furni (FurniData?, null until loaded) and G.GameData.Texts (ExternalTexts?).

FurniData : IReadOnlyCollection<FurniInfo>:
    this[string identifier] -> FurniInfo (throws if missing)
    GetInfo(ItemType, int kind) -> FurniInfo (throws)
    GetInfo(IItem) -> FurniInfo (throws)
    GetInfo(string identifier) -> FurniInfo (throws)
    TryGetInfo(…, out FurniInfo?) -> bool (non-throwing)
    Exists(ItemType, int) -> bool
    Exists(IItem) -> bool
    Exists(string identifier) -> bool (case-insensitive)
    FloorItemExists(int) -> bool
    WallItemExists(int) -> bool
    GetFloorItem(int) -> FurniInfo (throws)
    GetWallItem(int) -> FurniInfo (throws)
    FindItems(string) -> IEnumerable<FurniInfo> (by display name)
    FindFloorItems(string) -> IEnumerable<FurniInfo>
    FindWallItems(string) -> IEnumerable<FurniInfo>
    FindItem(string) -> FurniInfo? (best match or null)
    FindFloorItem(string) -> FurniInfo?
    FindWallItem(string) -> FurniInfo?
    state: FloorItems:IReadOnlyCollection<FurniInfo>, WallItems:IReadOnlyCollection<FurniInfo>, Count:int

FurniInfo (record, init-only):
    state: Type:ItemType, Kind:int (numeric type ID used in packets), Identifier:string (unique key e.g. "throne")
    state: Name:string, Description:string, DefaultDirection:int (0-7)
    state: XDimension:int, YDimension:int (tile footprint)
    state: OfferId:int, RentOfferId:int (-1=not sold)
    state: BuyOut:bool, RentBuyOut:bool, IsBuildersClub:bool, ExcludedDynamic:bool
    state: Category:FurniCategory, CategoryName:string (JSON/Unity only)
    state: CanStandOn:bool, CanSitOn:bool, CanLayOn:bool, IsUnwalkable:bool
    state: Line:string (furni line e.g. "xmas2023"), PartColors:ImmutableArray<string>
    state: Environment:string, IsRare:bool (JSON/Unity only; empty/false on Flash)
    state: Revision:int, CustomParams:string, AdUrl:string

The "poster" identifier covers all poster variants; the variant is the data value, not the kind.

ExternalTexts : IReadOnlyDictionary<string,string>:
    this[key] -> string (throws if missing)
    ContainsKey(string) -> bool
    TryGetValue(string, out string) -> bool
    state: Keys, Values, Count

Name-resolution extension methods on ExternalTexts:
    GetBadgeName(code) / TryGetBadgeName(code, out name)       key: badge_name_{code}
    GetBadgeDescription(code) / TryGetBadgeDescription         key: badge_desc_{code}
    GetEffectName(int id) / TryGetEffectName                   key: fx_{id}
    GetEffectDescription(int id) / TryGetEffectDescription     key: fx_{id}_desc
    GetHandItemName(int id) / TryGetHandItemName               key: handitem{id}
    GetHandItemIds(string name) -> IEnumerable<int>            reverse scan (do not call in tight loops)
    TryGetPosterName(variant, out) / TryGetPosterDescription   key: poster_{variant}_name / _desc

Item-level extension methods (available on IItem/IFurni/IInventoryItem):
    GetInfo() -> FurniInfo
    GetIdentifier() -> string
    GetName() -> string (falls back to "Type:Kind")
    TryGetName(out string) -> bool
    GetVariant() -> string
    GetCategory() -> FurniCategory
    GetLine() -> string

```csharp
var furni = G.GameData.Furni!;
var info = furni.GetInfo(ItemType.Floor, someItem.Kind);
Log($"{info.Identifier} \"{info.Name}\" {info.XDimension}x{info.YDimension}");
string? badge = GetBadgeName("BGHC"); // G member; prepends badge_name_ and looks it up in Texts
```

Cross-client notes: IFloorItem.State uses double.TryParse; IWallItem.State and IItemData.State use int.TryParse. Extra is 64-bit on Unity, truncated to 32-bit on Flash. CategoryName/Environment/IsRare on FurniInfo are populated only from JSON (Unity); empty/false on Flash/XML.

## Recipes & proven patterns

Distilled from the 213 user scripts. Every member is grounded in G.* / Xabbo.Core. Loop idiom is universal: while (Run) { try { ... } catch {} Delay(n); } where Run => !Ct.IsCancellationRequested. DelayAsync already passes Ct internally.

### Cross-cutting primitives

Block packet in intercept: call e.Block() synchronously before any await - the send decision is made before the first suspension.

Multi-header intercept:
```csharp
OnIntercept((In["A"], In["B"]), e => e.Block())
```
Tuple -> HeaderSet; one handler, many headers.

Raw/unmapped send:
```csharp
Send(new Packet(new Header(Destination.Server, (short)n), Client))
```
Use when no Out["Name"] resolves on this client.

Check header mapped: Messages.TryGetHeaderByValue(Destination.Server, Client, i, out Header h) - distinguish mapped vs unmapped before probing.

Keep script alive: Wait() (= Delay(-1)) - for pure event-driven scripts.

Blocking sleep / async sleep: Delay(ms) / await DelayAsync(ms) - Delay blocks the loop thread; both honor Ct.

Self projected position: (Self.CurrentUpdate.MovingTo ?? Self.Location).XY - true in-transit tile, not stale Self.Location.

Random free tile: Rand(Heightmap.Where(t => t.IsFree)) - Rand(collection) picks a random element.

Progress label / debug: Status(msg) / Log(obj) - non-intrusive UI status vs scripter console.

Point is a value type with an implicit (int,int) tuple cast, so HashSet<Point> tiles = new() { (13,13),(14,13) } works directly. At least 6 movement scripts paste the same Point : IEquatable<Point> boilerplate at the top.

### Game/dodge bots & smart movement

Self-position tracking via UserUpdate + /mv parse. The authoritative move target lives in the action string. Match Self.Index, regex /mv X,Y,Z/; CultureInfo.InvariantCulture is mandatory for the Z float (locale comma is a real bug).

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

Exemplars: ShowMoveMent, ColorSolver4, MovementTrackerSpeed, clickdetector.

Threat tracking via WiredMovements. Per movement: skip lead int, read FromX/FromY/ToX/ToY, two height strings, id, skip two ints. Velocity = (toX-fromX, toY-fromY); extrapolate N frames, expand by 1-2 tiles.

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

Exemplars: AntiCollisionGood, AvoidCollision2, Obsidian Maze, script-49 (full A* dodge), Sphere Runner V2-V5.

Walkable-tile precompute (8-dir, diagonals always legal). Build a Dictionary<Point,List<Point>> adjacency once at startup; never recompute per frame. Parse FloorPlan dynamically because the API varies - try fp.Heightmap (void = 'x') and fp.Tiles (blocked = sentinel 250) in separate try/catch.

Anticipated-position + danger scoring. getpos() returns confirmed /mv target, else last command if sent <250ms ago, else Self.Location. Score candidate moves: danger1 skip, danger2 -800..-1000, danger3 -400..-500, safe exits +20..+50 each, distance to goal -10..-100/manhattan. Stuck detection: if hist[0]==hist[2] && hist[0]!=hist[1] -> oscillating; after >2 pick a random/less-dangerous escape neighbor.

Furni-state-gated movement (mazes). Dictionary<Point, TileAction> keyed on RealPos, polled every ~100ms; TileAction holds main/else/Func<bool> condition checked against live furni (FloorItems.First(f => f.Id==X).State==0 / .Direction==4). Actions: Move(x,y), UseGate(furniId) (-> Out.EnterOneWayDoor), Talk(":exit").

Exemplars: MoveAction, Obsidian Maze, ObsidianMazeEasy, BanzaiTele-Bot, MazeRoom1.

Reaction/dodge from a held-item packet. In["CarryObject"] -> (userIndex, carryingId); on host holding item 3, Move(safeX, safeY).

Exemplar: nervous game.

### AI chat bridges (LLM over chat)

Trigger + throttle + typing skeleton. Room chat starting with +. OnChat(async e => ...), e.Message is text, e.Entity is the speaker (IRoomUser).

```csharp
OnChat(async e => {
    if (throttled || !e.Message.StartsWith("+")) return;
    if (DateTime.UtcNow - lastAsk < cooldown) { Sign(17); return; }
    if (HasBannedWords(e.Message)) return;
    lastAsk = DateTime.UtcNow;
    Send(Out["StartTyping"]);
    var profile = await Task.Run(() => GetProfile(e.Entity.Id)); // GetProfile blocks -> offload
    var reply = await CallLlm($"{persona} {Buildstate(e.Entity, profile)}", e.Message[1..]);
    Send(Out["CancelTyping"]);
    Shout(Sanitizenumbers(reply), 1014); // bubble 1014 is the de-facto default ("talkbuble")
});
```

Exemplars: 1Gpt-Smart(+Deepseek), ChatGpt V4, SmartAss V3, Gemini family, Ollama AI, Grok ChatGPT.

HTTP call shape (OpenAI-compatible). Same body for OpenAI/DeepSeek/Grok/Ollama (localhost:11434/v1/chat/completions); Gemini uses REST candidates[0].content.parts[0].text. Always using var client = new HttpClient() per call, System.Text.Json only (no Newtonsoft). Dual timeout guard:

```csharp
using var cts = new CancellationTokenSource(18000);
var task = client.PostAsync(endpoint, content);
if (await Task.WhenAny(task, Task.Delay(18000, cts.Token)) != task) return "timeout";
```

Context builder (Buildstate/roomfacts). Inject live state into the system prompt. Gender/HasRights are not on IEntity - read via reflection: e.Entity.GetType().GetProperty("Gender").GetValue(e.Entity). Profile fields are dynamic: .Friends (== -1 => hidden, guard before reading the rest), .ActivityPoints, .Created, .LastLogin, .Level, .StarGems, .IsFriend. Pull room facts from Room.Name, Room.FloorItems.Count(), Users ('{Name}':'{Motto}':'{Gender}').

Per-user chat ring buffer. Dictionary<string,List<string>>, cap 10 (some keep 5/35/45); compute the joined log inside the handler - computing formattedChatLog once at startup is a real bug (always empty).

Output hardening (all bots):
- Unicode allowlist (keep Latin + DE/PT/ES accents + punctuation + brackets for [CMD]): Regex.Replace(answer, @"[^a-zA-Z0-9\s\p{P}äöüÜÄÖß+=ÀàÃãÇçÉéÊêÍíÓóÔôÕõÚúÜü\[\]]", "")
- Long-number masking (anti-flag): split runs of >=5 digits with x - Regex.Replace(t, @"\d{5,}", m => string.Join("x", Enumerable.Range(0, m.Length/5).Select(i => m.Value.Substring(i*5,5))))
- Strip <think>...</think> for reasoning models (Ollama/DeepSeek).

In-band action protocol. Parse [CMD:DANCE] / bare [DANCE] from the reply, dispatch, then strip tags before Shout. Verified action calls: Dance(1), Wave(), Sit()/Stand(), Trade(target.Index), Send(Out["IgnoreUser"], id). [LASER] -> Talk(":yyxxabxa") (client effect cheat code). Newer V3/V4 use a typed mini-DSL (command:"Move",i:{x},i:{y}) parsed by regex -> Send(Out["Move"], ...).

Flood/mute sign-loop. Read duration int, set throttled, show a sign until elapsed, then clear:

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

DM bridge. Auto-accept (In["NewFriendRequest"] -> int userId, string name -> AcceptFriendRequests(new[]{userId})), then SendMessage(id, text). Long replies chunk at 125 chars with 500ms gaps. Echo your own DM into your client: Send(In.MessengerNewConsoleMessage, userId, "> "+text, 0, "").

Avatar mimic. Send(Out["UpdateFigureData"], "M", e.Entity.Figure), wait 8500ms (no confirm event), restore. Hardcoded "M" copies female avatars as male - a known wart.

Other bridges: Discord webhook (habbo to discord, HTC_DC_SCRIPT) forwards In.Chat to a webhook; Base64Bot (HTC_BASE64Bot) encodes outgoing / decodes incoming in try/catch; Grok UserImage builds the avatar image URL from the figure string for vision models.

### Mass furni placement / use

- Place from inventory: EnsureInventory(); foreach (var i in Inventory.Named("X")) { Place(i, tile.Location); Delay(35-150); }
- Bulk place w/ rotation: Place(roller, new Point(7,10), rot); rot += 2;
- Auto-pickall threshold: if (++placed >= 53) { Talk(":pickall"); placed = 0; } (BC: Shout(":pickallbc", 0))
- Mass use/toggle: foreach (var f in FloorItems.Where(x => x.GetName()==name)) Send(Out["PresentOpen"], (int)f.Id); Delay(1000);
- Wall placement: Move(wallItem, $":w=X,Y l=X,Y l") / Send(Out["PlacePostIt"], id, ":w=0,7 l=8,27 l") - colon prefix mandatory
- BC mass place: Send(Out.BuildersClubPlaceRoomItem, categoryId, offer.Id, "", x, y, dir)
- Recycle: Send(Out["RecycleItems"], 8, id0..id7) - exactly 8 IDs as separate args, IDs negated -(int)i.Id

Inventory load: EnsureInventory(timeoutMs) (required before any Inventory.*) or low-level new Xabbo.Core.Tasks.GetInventoryTask(Interceptor, true).Execute(60000, Ct). Filter by extra data: (item.Data as MapData)?["rarity"] == "0".

Always block server noise during automation: OnIntercept((In.ErrorReport, In["MarketPlaceOffers"], In["MarketplaceMakeOfferResult"], In["FurniListInvalidate"], In["UnseenItems"]), e => e.Block()).

Throttle 35-300ms/op (35-150 floor, 200 wall, 1s social, 50ms scan) or the server drops items.

Exemplars: PlaceSeedRoom, PlaceSeedToRoomSimple, PlacePlantPickupSeed, OpenBox, Recycler, placebc, PlaceBCWall, POST-IT-LEVEL, UseFloorItemMass.

Pet breeding loop. Suppress pet/inventory spam, then pair customize + breed:

```csharp
OnIntercept((In.PetBreedingResult, In.PetStatusUpdate, In["FurniListInvalidate"], In["UnseenItems"]), e => e.Block());
while (Run) foreach (var pet in Pets) {
    Send(Out["CustomizePetWithFurni"], potionId, pet.Id); Delay(500);
    Send(Out.BreedPets, 0, pet.Id, pet.Id); Delay(1000);
}
```

Exemplars: BreedSeed(V2), SimpleBreedV1.

### Room / catalog / market scanning & scraping

Catalog walk. GetCatalog() (or GetBcCatalog()) -> nodes; per node GetCatalogPage(node) (network call, pace 150-300ms), flatten page.Offers -> offer.Products, match product.GetIdentifier(). Distinguish currency via offer.PriceInActivityPoints / ActivityPointType. Serialize with JsonSerializer + JavaScriptEncoder.UnsafeRelaxedJsonEscaping to avoid escaping UTF-8.

```csharp
foreach (var node in GetCatalog().Where(x => x.Id > 0)) {
    Status($"..."); var page = GetCatalogPage(node);
    foreach (var offer in page.Offers) foreach (var p in offer.Products)
        if (p.GetIdentifier() == target) { /* hit */ }
    await Task.Delay(300, Ct);
}
```

Exemplars: CatalogScraper, placebc, PlaceBCWall (joins furnidata_json/0 for colors).

Marketplace. SearchMarketplace("", 1, 10, MarketplaceSortOrder.LowestPrice, timeout: 15000) -> IMarketplaceOffer (.Id cast to int, .Price); buy with Send(Out.MarketplaceBuyOffer, (int)offer.Id). Sniper polls SearchMarketplace(info.Name).OfKind(id) and buys under threshold every ~400ms. Stats sweep: Send(Out.GetMarketplaceItemStats, spriteId, 1) then await ReceivePacket(In.MarketplaceItemStats, Ct), 200ms throttle. Auto-relist: on In.MarketplaceSaleSuccess re-send Out.PlaceItemInMarketplace.

Exemplars: ShopSniper, MarketScan, MarketSales Bot, GetOfferID.

Multi-room furni scanner. SearchNavByOwner(name).Where(r => r.IsOpen), EnsureEnterRoom(r.Id) == RoomEntryResult.Success, then Furni.OfKind(FurniData.FindFloorItem(name)).Any(); 2500ms between rooms.

Exemplars: seachfurniscript, MP Adventure, Claude Takeover.

Room -> HTTP bridge. On OnEnteredRoom + object/user intercepts, POST room state JSON to a local endpoint; wrap each item access in try/catch (items can be null mid-enumeration); 15s polling fallback; dispose HttpClient on stop.

Exemplar: Shroom.

Profile / user scraping. Send(Out["GetExtendedProfileByName"], name, false), intercept In["ExtendedProfile"], read fields in exact order; guard optional trailing bytes with packet.Length - packet.Position > 0 + per-field try/catch. Username->index map from the Users packet (read all entities, skip fields sequentially).

Exemplars: ReadUserProfile, FetchUsersDate, GetUserIndexID, UserInfo Export.

### Puzzle solvers

Grid read (shared base). Build Dictionary<(int gx,int gy), long tileId> at startup, then re-query FloorItems.FirstOrDefault(f => f.Id == tileId) to read current item.State - avoids re-scanning by position. Grid offset constants OX/OY. After each move, confirm with WaitForTileChange (poll state, ~5ms x up to 200) rather than fixed delay.

- ColorSolver1: randomized heuristic (8000 tries / 4s), greedy fallback; move via Send(Out["Move"], x, y) @55ms
- ColorSolver2/3: A* (Chebyshev, zeros=walls), SortedSet<(f,seq,x,y)> PQ; Move, poll state to confirm
- ColorSolver4: Fleury bridge-detection + Warnsdorff, UserUpdate true-pos tracking; Move
- FloodIt / Sirjonas-Floodit: greedy lookahead seed -> parallel IDA* (Parallel.ForEach, lock on globalBest, 4000ms cap); Send(Out["ClickFurni"], id, 0) + WaitForFlood
- B3R1 / 1B3R1: backtracking (self-referencing Func<bool> solve=null; solve=()=>..., goto found to exit nested loops); ClickFurni x2 (pick up) + Move to target
- Domino V1->V4: greedy -> backtracking -> constraint solver; board synced via In["ObjectAdd"]; clear board on ObjectRemove (v3 fix); place via packets
- Tetris/Tetris2: column scoring (line-clears - holes); render dirty cells only via Send(Out["UseFloorItem"], id, state)
- Snake: LinkedList<(x,y)> body, place/remove floor item per tick

Pixel art / color paint. Detect color from furni name suffix Regex.Match(name, @"\d+$"). Sort target tiles by color to minimize selector switches: click selector furni only when color changes, then click each target tile (50ms gaps). Source area maps to field area by offsetY = SRC_MIN_Y - FIELD_MIN_Y. 3-attempt re-scan to catch misses.

Exemplars: Click-PixelArt(-Automatic), MakeRIPV2, SVG_Tracer, TextTracer (bitmap font Dictionary<char,int[]>).

Drop-in game state (FridgeCheat etc.): intercept the game-state packet -> solve client-side -> replay moves with delay.

### Trading automation

Offer + accept flow. Trade(targetIndex) / Send(Out["OpenTrading"], userId) -> Send(Out["TradingOffer"], furniId) -> Send(Out["AcceptTrade"]) -> Send(Out["ConfirmTrade"]), 300-500ms between steps. Register the intercept before the request to avoid a race.

Bulk add: Send(Out.TradeAddItems, items.Select(x => (int)x.ItemId).ToList()) - one List<int>.

Auto-accept loop. Trader.csx waits per partner via a TCS gate:

```csharp
async Task<IPacket?> WaitPacket(Header h, int ms) {
    var tcs = new TaskCompletionSource<IPacket>();
    using var _ = ... OnIntercept(h, e => tcs.TrySetResult(e.Packet));
    try { return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ms), Ct); } catch { return null; }
}
```

Exemplars: Trade Accept, Trader, Tradepass(_Unity), PlaceToTrade.

### Packet tracing & header discovery

Unmapped header probe. Iterate a range; skip mapped via Messages.TryGetHeaderByValue(Destination.Server, Client, i, out _); send the rest as new Packet(new Header(Destination.Server, i), Client). Reflect over Out properties to recover names. Resumable scanners (ParseUnityHeader*) burst 50x per header @5ms, watch for a known response header, and persist a checkpoint so a disconnect resumes.

Exemplars: 1SendNonMappedHeaders, SendHeaderUnkownPackets, SendHeaderNotMapped, FindHiddenHeader, ParseUnityHeader(File), DangerZone.

One-shot field sniffer: ex: OnIntercept(Out.EnterRoom, e => Log($"{e.Packet.ReadInt()} {e.Packet.ReadString()}")). Raw hex dump: ex: Log($"[{e.Packet.Header}] {BitConverter.ToString(e.Packet.ToArray())}")

Intercept-rewrite. Capture a value, block, re-emit transformed: e.g. PlayFreeze maps Out["ClickFurni"] -> Out["UseFurniture"]; MoveUserTo blocks an avatar click + the next Move, then teleports a target via negative wired variable IDs (Send(Out["WiredSetObjectVariableValue"], 1, idx, "-270"/"-230"/"-231", val) with 250ms gaps).

### Effects, dances, misc

Action sequence: Dance(1); Wave(); Sign(3); ThumbsUp(); Sit(); Stand(); Talk(text); spaced by Delay. Dance(0)/StopDancing() stops.

Movement effect: ActivateEffect(id) / EnableEffect(id), restore with ... 0 after a delay.

Friend-request DM onboarding, MimicLook figure spoof, SendVisibleMessage self-echo (see AI section - same helpers).

Shell-out: write a temp .js, Process.Start("node", ...) with RedirectStandardOutput, fire a game Send in parallel, then WaitForExit and inspect stdout for a known prefix (Minter). Embedded HttpListener + inline HTML UI controls a batch loop and auto-opens the browser (Recycler).

### Pitfalls (verified, recurring)

- e.Block() decided after an await -> call it on the first synchronous line of the handler
- Z-float parsed with locale comma -> double.Parse(s, CultureInfo.InvariantCulture)
- formattedChatLog computed once at startup -> build the joined log inside OnChat
- Log($"Timeout for {e} seconds") logs the event object -> log the duration int
- var extravar = $"...{Language}" before var Language=... -> declare Language first (forward-ref -> empty string)
- GetProfile/inventory blocking the chat handler -> await Task.Run(() => GetProfile(id)); EnsureInventory before Inventory.*
- UserUpdate is a batch packet -> loop all entities, skip every field even for non-targets
- Stale Self.Location mid-walk -> Self.CurrentUpdate.MovingTo ?? Self.Location
- Reading past packet end on optional fields -> guard packet.Length - packet.Position > 0
- Sending furni-from-inventory with positive ID -> negate: -(int)item.Id
- Too-fast sends -> server drops/kick: 35-150ms furni, 200ms wall/market, 1s social, 50ms scan
- Flash vs Unity arg-count divergence -> branch on Client / Session.Is(ClientType.Unity)

## Debugging & issue-spotting playbook

### Triage tools (read in this order)

get_errors -> Roslyn compile diagnostics (line,col): error CSxxxx: message
    First thing after any edit_tab/run_code that fails to start. Pure compile-phase.

get_script_status -> ScriptStatus enum + IsFaulted
    Confirm whether a run reached Running, ended Complete, or died
    CompileError/RuntimeError/TimedOut/Canceled/Aborted.

get_script_log -> per-script output: Log(...)/Status(...) lines, return value, runtime exception (script frames only, filename:line N)
    Diagnose runtime faults. Xabbo.* stack frames are filtered; only your .csx frames show.

get_app_log -> host/interceptor-level log
    Connection drops, interceptor not attached, data (FurniData/FigureData) load failures,
    things that never reach your script.

ScriptStatus decoder:
- CompileError -> read get_errors
- RuntimeError (IsFaulted=true) -> get_script_log for the exception
- TimedOut -> OperationCanceledException fired without you cancelling: a Receive/Ensure* blew
    its timeout, or host killed a stuck script
- Canceled -> you called Finish() or the user stopped it
- Aborted -> thread hard-killed

### Compile errors -> cause -> fix

CS0103: The name 'X' does not exist
    Member not on G, or namespace not imported. Verify against the API; default imports already
    cover System.Linq, Xabbo.Core, etc. -- do not add using for those.

CS1061: 'IFloorItem' has no definition for 'GetName'
    Extension method needs its type arg. ex: item.GetName(FurniData) -- pass FurniData;
    extensions live in Xabbo.Core.Extensions (imported).

CS0246: type 'Point' not found
    Point is not built in. Either use Xabbo.Core Tile/(int,int) tuples, or paste the custom
    Point struct (see Idioms).

CS1503: cannot convert 'int' to 'Header'
    Passed a raw short where a Header is expected. Use In.Chat/Out.Move or In["WiredMovements"]
    indexer, never a bare number.

CS4033 / CS1996: await in non-async
    Used await in a sync lambda passed to OnIntercept/OnChat. Make the lambda async e => { ... }
    -- the Func<...,Task> overload exists for every On*.

CS0815 / CS8130: cannot assign void
    Assigned a void G method (Move, Talk) to a var. These return void; only
    Receive/Get*/Search* return values.

CS0019: operator '==' cannot be applied to 'Point'
    Custom Point missing operators. Paste the full struct with ==/!=/Equals/GetHashCode.

### Runtime faults & pitfalls -> fix

NullReferenceException on Room/Self/Heightmap
    Not in a room; Room is IRoom?, Self is IRoomUser?, RoomId == -1.
    Fix: if (!IsInRoom || Self == null) { Log("not in room"); return; }
    Self is null until entities load even after entering.

Script status stuck Running, won't stop
    Ct not honored: tight loop or blocking call ignores cancellation.
    Fix: loop on while (Run) (= !Ct.IsCancellationRequested); use Delay(ms) (throws on cancel)
    not Thread.Sleep; pass Ct/timeout to every async op. RunTask bodies must poll Run + Delay
    as exit points.

TimedOut immediately on EnsureInventory/Receive/Get*
    Server never replied: not in room, wrong header, or feature N/A.
    Fix: EnsureInventory needs you in a room. For Receive, confirm the header matches the actual
    incoming packet and Client. Bump timeout or use TryReceive (returns false on timeout, but
    Ct still throws).

await "does nothing" / fires after script ends
    Async G call not awaited (SendAsync, DelayAsync, ReceiveAsync) returns Task/ValueTask
    discarded.
    Fix: await it, or use the sync overload (Send, Delay, Receive).

Chat sent but emoji/special chars missing
    Server strips non-allowlisted chars from Talk/Shout.
    Fix: pre-sanitize: ex: Regex.Replace(text, @"[^a-zA-Z0-9\s\p{P}äöüÜÄÖß+=]", "")
    Long digit runs also flagged -- mask \d{5,}.

Receive/intercept reads garbage or throws on ReadX
    Wrong field type/order, or Flash<->Unity layout differs.
    Fix: read fields in exact wire order with the right ReadInt/ReadString/etc. Float coords
    are sometimes string (e.g. UserUpdate z is ReadString).

Same code, different packet shape per client
    Client is Flash/Unity/Shockwave; layouts diverge (e.g. PlaceFloorItem).
    Fix: prefer G helpers (PlaceFloorItem, PlaceWallItem) which branch on Client internally.
    For raw packets, branch on Client yourself; resolve headers via In["Name"]/Out["Name"]
    not hardcoded shorts.

Header not found / TryGetHeaderByValue false
    Unmapped or wrong Destination/Client.
    Fix: Messages.TryGetHeaderByValue(Destination.Server, Client, short, out var h) to test;
    named headers (Out.Chat) are safest.

Float parsed wrong (1.5 -> 15 or throws)
    Locale comma/dot mismatch.
    Fix: always double.Parse(s, CultureInfo.InvariantCulture).

Sent packet looks injected to client but hits server (or vice-versa)
    Direction confusion.
    Fix: Send(Out.*) -> server; to fake an incoming packet to the client use
    Interceptor.Send(In.*, ...) (e.g. ShowBubble, fake DM echo via In.MessengerNewConsoleMessage).

Disconnect / flood mute mid-loop
    Too-fast sends (chat, moves, purchases) trip server rate limits.
    Fix: throttle with Delay() between sends (chat >= ~1.5s); guard with
    DateTime.UtcNow - last < ratelimit. Handle In.FloodControl/In.MuteTimeRemaining (carry a
    duration int) and back off.

Exception in hot loop kills script
    Unguarded ReadX on malformed packet at 50Hz.
    Fix: wrap the per-iteration body in try { ... } catch { } and Delay(20) -- standard
    collision-bot pattern.

UserDuckets wrong
    Returns hardcoded 10 when ducket point type absent.
    Fix: read UserPoints[ActivityPointType.Ducket] directly and handle missing.

SetUserFigure(string) throws
    Figure resolves to Unisex gender.
    Fix: use the 2-arg SetUserFigure(figure, gender) overload.

Event handler logs the wrong value
    Logging the event object instead of the parsed field (e.g. Log($"...{e}...")).
    Fix: log the extracted local (Log($"...{duration}...")), not e.

FloorPlan.Tiles vs .Heightmap throws
    Property availability depends on client.
    Fix: dynamic fp = FloorPlan; then try { x = fp.Tiles; } catch {} and
    try { y = fp.Heightmap; } catch {} -- handle whichever exists. Heightmap void tile = 'x';
    Tiles blocked sentinel = 250.

### Probe loop (fast iterate without polluting tabs)

1. Probe with run_code (hidden) -- run a throwaway snippet to inspect live state before
   committing to a tab:

```csharp
Log($"in={IsInRoom} room={RoomId} self={Self?.Index} client={Client}");
Log($"users={Users.Count()} furni={Furni.Count()}");
var p = Receive(In.Whisper, timeout: 5000, block: true);
Log(p.ReadString());
```

   Read result via get_script_log. Hidden runs don't create a saved tab -- use them to
   discover packet layouts, confirm headers, dump ToJson(...) of an object, or check
   get_errors on a fragment.

2. Confirm packet shape before trusting it: probe a single Receive, Log each ReadX in order,
   adjust types until the dump is sane.

3. Promote to a tab with edit_tab once the snippet works, then rerun. On failure:
   get_errors (compile) -> fix -> edit_tab -> rerun;
   get_script_status + get_script_log (runtime) -> fix -> edit_tab -> rerun.

4. Keep loops cancel-safe while iterating: always while (Run) + Delay, so a stuck probe is
   stoppable instead of forcing an Aborted hard-kill.

## Cheat sheet & gotchas

Everything below is a direct member of the globals class G (or a Xabbo.Core type) and is in scope unqualified inside a .csx script.

### Top one-liners

state: Run(bool=!Ct.IsCancellationRequested), IsFinished(bool=true only after Finish()), IsInRoom, IsLoadingRoom, IsRingingDoorbell, IsInQueue, QueuePosition:int, RoomId:long(-1=none), Room:IRoom?, DoorTile:Tile?, Heightmap:IHeightmap?, FloorPlan:IFloorPlan?

state: Self:IRoomUser?(null until own entity arrives in room), UserId, UserName, UserCredits, UserDiamonds, UserDuckets(fallback 10 on Shockwave), Client:ClientType

state: Entities:IEnumerable<IEntity>, Users:IEnumerable<IRoomUser>, Pets:IEnumerable<IPet>, Bots:IEnumerable<IBot>(all empty when not in room, never null), Furni, FloorItems, WallItems(same)

state: In:Incoming, Out:Outgoing(header tables - In.Whisper, Out.Chat, In["WiredMovements"])

Delay(int ms) -> void
    blocking sleep; throws OperationCanceledException on cancel - the cooperative exit point

DelayAsync(int ms) -> Task  [async]
    awaitable sleep

Wait() -> void
    Delay(-1); parks script alive; keeps intercept/event handlers running until cancelled

Finish() -> void
    cancels Ct, sets IsFinished=true, throws OperationCanceledException at call site; use from callbacks to stop the script

Error(string msg) -> ScriptException
    returns (does not throw) - write: throw Error("msg")

Log(object? obj) -> void
    append to output panel

Status(object? obj) -> void
    update status line in script list

RunTask(Action action) -> void
    fire work on thread pool; body must poll Run and call Delay as exit points or outlives the script

ToJson(object? o, bool indented=true) -> string
FromJson<T>(string s) -> T?

InitGlobal(string name, dynamic value) -> bool
    create cross-script shared var only if absent; returns true if created

Distance(Point a, Point b) -> double
    static; Euclidean

Move(int x, int y) -> void
    fire-and-forget walk request; server drives steps; no arrival confirmation

Move(Point p) -> void
    same as above

LookTo(int x, int y) -> void
    face a tile without moving

Turn(int dir) -> void
Turn(Directions dir) -> void

Talk(string message, int bubble=0) -> void
Shout(string message, int bubble=0) -> void
Whisper(IRoomUser recipient, string message, int bubble=0) -> void
Whisper(string recipient, string message, int bubble=0) -> void
    both overloads prepend the recipient name automatically - do not prepend yourself

Chat(ChatType chatType, string message, int bubble=0) -> void

ShowBubble(string msg, int? index=null, int bubble=30, ChatType type=Whisper) -> void
    client-side only; injects an incoming packet; server never sees it; index defaults to Self?.Index ?? -1

Action(int id) -> void
Action(Actions action) -> void
Wave() -> void
ThumbsUp() -> void
Idle() -> void
Unidle() -> void
Sit() -> void
Sit(bool sit) -> void
Stand() -> void
Dance() -> void
Dance(int id) -> void
Dance(Dances d) -> void
StopDancing() -> void
Sign(int id) -> void
Sign(Signs s) -> void

Actions: None=0, Wave=1, Kiss=2, Laugh=3, Idle=5, Jump=6, ThumbsUp=7
    Kiss/Laugh/Jump have no shortcut - use Action(Actions.Kiss) etc.

Dances: None=0, Dance=1, PogoMogo=2, DuckFunk=3, TheRollie=4
    Dance() uses id 1; Dance(0)/StopDancing() stops

Signs: None=-1, Zero=0, One=1 ... Ten=10, Heart=11, Skull=12, Exclamation=13, SoccerBall=14, Smile=15, RedCard=16, YellowCard=17

EnableEffect(int effectId) -> void
    equip/display an already-owned effect; sends Out.UseAvatarEffect

DisableEffect() -> void
    sends Out.UseAvatarEffect(-1)

ActivateEffect(int effectId) -> void
    inventory action - CONSUMES a non-permanent effect; use EnableEffect to just display

GetUser(string name) -> IRoomUser?
GetUser(int index) -> IRoomUser?
GetEntityById<T>(long id) -> T?
GetBot(string name) -> IBot?
GetPet(string name) -> IPet?
    all lookups return null if absent; GetUser(string) name match is case-sensitive

Respect(long userId) -> void
Respect(IRoomUser user) -> void
Ignore(IRoomUser user) -> void
Ignore(string name) -> void
Unignore(IRoomUser user) -> void
Unignore(string name) -> void
FriendRequest(IRoomUser user) -> void
FriendRequest(string name) -> void
Scratch(long petId) -> void
Scratch(IPet pet) -> void
Mount(long petId) -> void
Mount(IPet pet) -> void
Dismount(long petId) -> void

GetFloorItem(long id) -> IFloorItem?
GetWallItem(long id) -> IWallItem?

UseFurni(IFurni f) -> void
ToggleFurni(IFurni f, int state) -> void
UseFloorItem(long id) -> void
ToggleFloorItem(long id, int state) -> void
UseWallItem(long id) -> void
ToggleWallItem(long id, int state) -> void
UseGate(long id) -> void
UseGate(IFloorItem f) -> void

Place(IInventoryItem item, Point point, int dir=0) -> void
Place(IInventoryItem item, WallLocation loc) -> void
    uses item.ItemId (inventory slot), not item.Id - the overload handles this correctly

PlaceFloorItem(long itemId, Point point, int dir=0) -> void
PlaceWallItem(long itemId, WallLocation loc) -> void
PlaceWallItem(long itemId, string location) -> void
    both branch on Client internally; Shockwave throws "Unknown client protocol."

Move(IFloorItem item, Point point, int dir=0) -> void
Move(IWallItem item, WallLocation loc) -> void
Move(IWallItem item, string loc) -> void
MoveFloorItem(long id, Point point, int dir=0) -> void
MoveWallItem(long id, WallLocation loc) -> void

Pickup(IFurni f) -> void
PickupFloorItem(long id) -> void
PickupWallItem(long id) -> void
DeleteWallItem(IWallItem item) -> void
DeleteWallItem(long id) -> void
    for stickies/photos; sends Out.RemoveItem

UpdateStackTile(IFloorItem item, float height) -> void
UpdateStackTile(long id, float height) -> void
    height in tile units (1.0f = one tile); sent x100 internally; sends Out.StackingHelperSetCaretHeight

EnsureInventory(int timeout=30000) -> IInventory
    blocking; must be in a room or server never responds; throws on timeout

Trade(IRoomUser user) -> void
Trade(int userIndex) -> void
Offer(IInventoryItem item) -> void
Offer(long itemId) -> void
Offer(IEnumerable<IInventoryItem> items) -> void
Offer(IEnumerable<long> itemIds) -> void
CancelOffer(IInventoryItem item) -> void
CancelOffer(long itemId) -> void
AcceptTrade() -> void
ConfirmTrade() -> void
CancelTrade() -> void

EnsureEnterRoom(long roomId, string password="", int timeout=10000) -> RoomEntryResult
    blocking; confirmed entry; rewrites packets in-flight
    RoomEntryResult: Unknown, Full, Banned, InvalidPassword, Success
    InvalidPassword is defined but not currently produced - wrong password yields Unknown

EnterRoom(long roomId, string password="") -> void
    fire-and-forget Out.FlatOpc; no confirmation; only works if navigator already loaded that room

LeaveRoom() -> void

Send(IReadOnlyPacket packet) -> void
Send(Header header) -> void
Send<T1..Tn>(Header header, T1 a1, ...) -> void
    type-dispatch per arg; see client-difference table in Packets section

Receive(HeaderSet headers, int timeout=-1, bool block=false) -> IReadOnlyPacket
Receive(ITuple headers, int timeout=-1, bool block=false) -> IReadOnlyPacket
    blocking; timeout=-1 means wait forever; block=true drops the packet; throws TimeoutException on timeout (not OperationCanceledException)

TryReceive(HeaderSet headers, out IReadOnlyPacket? packet, int timeout=-1, bool block=false) -> bool
    does NOT swallow TimeoutException - wrapping Receive in try/catch is safer for timeout-as-false cases

OnIntercept(Header header, Action<InterceptArgs> callback) -> void
OnIntercept(ITuple headers, Action<InterceptArgs> callback) -> void
OnIntercept(Header header, Func<InterceptArgs,Task> callback) -> void
    async overload is fire-and-forget - exceptions inside are silently swallowed; call e.Block() synchronously before any await

ModifyRoomSettings(Action<RoomSettings> updater, long? roomId=null, int timeout=10000) -> void
    fetch -> mutate -> save; defaults to current room; requires ownership

GetRoomData(long roomId, int timeout=10000) -> IRoomData
GetRoomSettings(long roomId, int timeout=10000) -> RoomSettings
    requires ownership; times out otherwise

SearchNav(string category, string filter="", int timeout=10000) -> IEnumerable<IRoomInfo>
SearchNavByOwner(string ownerName, int timeout=10000) -> IEnumerable<IRoomInfo>
SearchNavByName(string roomName, int timeout=10000) -> IEnumerable<IRoomInfo>
SearchNavByTag(string tag, int timeout=10000) -> IEnumerable<IRoomInfo>
SearchNavByGroup(string group, int timeout=10000) -> IEnumerable<IRoomInfo>
QueryNav(string query, int timeout=10000) -> IEnumerable<IRoomInfo>

GetProfile(long userId, int timeout=10000) -> IUserProfile
SearchUser(string name, int timeout=10000) -> UserSearchResult?
SearchUsers(string name, int timeout=10000) -> UserSearchResults

GetBadgeName(string code) -> string?
GetEffectName(int id) -> string?
GetHandItemName(int id) -> string?
GetHandItemIds(string name) -> IEnumerable<int>
    full-scans Texts; do not call in tight loops

SetUserMotto(string motto) -> void
SetUserFigure(string figure, Gender gender) -> void
SetUserFigure(string figure) -> void
    single-arg infers gender via Figure.Parse; throws if Unisex - use 2-arg overload to be safe

Rand() -> int
Rand(int max) -> int
    max is exclusive

Rand(int min, int max) -> int
    max is exclusive

Rand<T>(IEnumerable<T> source) -> T
    returns default (possibly null) on empty

Rand<T>(T[] source) -> T
    throws if empty

RandDouble() -> double

ShowBubble is listed above; also: SetDarkTheme(bool), SetBackgroundColor(byte r, byte g, byte b) - scripter window UI only

### Units & coordinate conventions

- X = column (west-east), Y = row (north-south), both int
- Point(x,y); (int,int) tuples auto-convert: Move((5,5)), new HashSet<Point> { (13,13), (14,13) }
- Z / height: IFloorItem.Z is double stack height in tile units (~0.5 per stack tile); UpdateStackTile takes float in tile units, sent x100 internally
- Directions: 0-7 clockwise from North; Directions: North=0, NorthEast=1, East=2, SouthEast=3, South=4, SouthWest=5, West=6, NorthWest=7; furni Direction typically uses even values (0,2,4,6)
- Timeouts: ms; -1 = infinite; most blocking calls default 10000, inventory/pet 30000

### Sharpest gotchas

- Move never awaits arrival - no WalkTo exists; to know you arrived, poll Self.Location or intercept In["UserUpdate"]; authoritative target is the /mv X,Y,Z/ action string in UserUpdate
- Float parsing locale: when parsing Z/heights from packet strings always pass CultureInfo.InvariantCulture - a comma-locale machine will misparse "1.5"
- Delay is the cancellation checkpoint: tight loops with no Delay/DelayAsync cannot be cancelled; inside RunTask call Delay periodically or check Run or the task outlives the script
- Finish() throws immediately at the call site - code after it in the same call stack never runs; Finish() and external Stop both produce OperationCanceledException; IsFinished is the only way to distinguish them
- Error("msg") does not throw - write: throw Error("msg")
- Self is room-scoped: null when not in room; use Self?.Index before accessing; UserId/UserName are account-level and always available once profile loads; Self is null until the own entity appears even after entering the room
- Throwing properties: UserData, UserCredits, UserPoints, UserAchievements, FigureData throw if not yet loaded; Room, Inventory, DoorTile return null instead
- EnsureEnterRoom vs EnterRoom: EnsureEnterRoom rewrites packets in-flight and blocks for confirmation; EnterRoom is fire-and-forget and only works if navigator already loaded that room's data; always branch on the result:

```csharp
var r = EnsureEnterRoom(12345678);
if (r != RoomEntryResult.Success) throw Error($"Entry failed: {r}");
```

- EnsureInventory requires being in a room: server only answers inventory requests from inside a room; calling it in hotel view hangs to timeout; re-call when Inventory.IsInvalidated
- ItemId vs Id: pass ItemId (inventory slot) to Offer/Place - Id is the in-room furni instance ID and is a different value; Place(IInventoryItem,...) uses ItemId automatically; PlaceFloorItem/MoveFloorItem take itemId explicitly
- Wall vs floor data are different systems: IFloorItem.Data is IItemData (structured); IWallItem.Data is a plain string - no .Value/.Type on a wall item
- State parsing differs: FloorItem.State uses double.TryParse (handles "1.0"); WallItem.State and IItemData.State use int.TryParse; all return -1 when unparseable
- Receive throws TimeoutException on timeout (not OperationCanceledException); TryReceive does not swallow TimeoutException - wrap Receive in try/catch for timeout-as-false logic:

```csharp
IReadOnlyPacket? chat = null;
try { chat = Receive((In.Chat, In.Shout), timeout: 5000); }
catch (TimeoutException) { Log("no chat in 5s"); }
```

- OnIntercept async overload (Func<InterceptArgs,Task>) is fire-and-forget - exceptions inside are silently swallowed and e.Block() called after an await is too late; always call e.Block() synchronously before any await
- Do not retain InterceptArgs past the callback - it is disposed by the framework afterward
- Send<T...> float trap: passing a C# float literal calls WriteFloat (raw 4-byte), which Flash does not expect; for cross-client float fields pass LegacyFloat; a C# long becomes a LegacyLong (truncates on Flash) - pass int if the value is really 32-bit
- ReadLong/WriteLong throw on Flash - use ReadLegacyLong/WriteLegacyLong; Read<float>() calls ReadFloat (raw), not legacy; for cross-client float fields use ReadLegacyFloat()/Read<LegacyFloat>()
- Place/PlaceWallItem are client-specific internally - G handles it; if you hand-roll Out.PlaceRoomItem you must branch on Client
- ShowBubble is fake: injects an incoming chat packet (client display only); does not send to server; use Talk/Shout to actually speak
- Trade has no EnsureTrade: Trade(user) is fire-and-forget; subscribe to OnTradeOpened/OnTradeOpenFailed or poll IsTrading before calling Offer; two-stage flow: AcceptTrade() -> wait for IsTradeWaitingConfirmation -> ConfirmTrade(); any offer change after acceptance resets accepted state - re-accept
- Move is overloaded: Move(int,int)/Move(Point) walks the avatar; Move(IFloorItem,Point,dir)/Move(IWallItem,WallLocation) relocates furni - pick the right overload
- Xabbo.Core.Point already exists and supports tuple conversion - only define a local struct Point if you need IEquatable/hashing not on the core type; do not shadow it in scripts that call Move
- Global is shared across all running scripts - use InitGlobal to avoid start-up races; missing keys return null
- GetUser(string) name match is case-sensitive
- UserDuckets returns hardcoded 10 when the ducket point type is absent (Shockwave) - read UserPoints[ActivityPointType.Ducket] directly and handle missing
- SetUserFigure(string) throws if the figure resolves to Unisex - use SetUserFigure(figure, gender) overload
- FurniData["identifier"] indexer throws if missing - use TryGetInfo; matching is case-insensitive
- GetGroupMembers returns one page (0-based): loop p until (p * PageSize) >= TotalEntries
- GetBadgeName is preferred over TryGetBadgeName: the TryGet variant queries the raw code without the badge_name_ prefix - inconsistent
- GetHandItemIds full-scans Texts - do not call in tight loops
- Logging an event object instead of the parsed field: Log($"...{e}...") logs the object; extract the value first: Log($"...{duration}...")
- FloorPlan.Tiles vs .Heightmap availability depends on client: use dynamic fp = FloorPlan; try { x = fp.Tiles; } catch {} and try { y = fp.Heightmap; } catch {} - void tile = 'x'; blocked sentinel = 250
- UserUpdate is a batch packet: loop all entities, reading every field even for non-targets; skip positions for those you do not process
- Self.Location may be stale mid-walk: use Self.CurrentUpdate.MovingTo ?? Self.Location for the authoritative in-transit tile
- Reading past packet end on optional fields: guard with packet.Length - packet.Position > 0 before each optional ReadX
- Too-fast sends trip server rate limits: 35-150ms furni, 200ms wall/market, ~1.5s chat, 1s social, 50ms scan

### Idioms

```csharp
// enter, walk, face, speak
if (EnsureEnterRoom(12345678) != RoomEntryResult.Success) throw Error("no entry");
Move(5, 5); Delay(2000); Turn(Directions.North); Talk("here");

// react to room chat until finished
OnChat(e => { if (e.Message == "stop") Finish(); else Log($"{e.Entity.Name}: {e.Message}"); });
Wait();

// raw intercept: read a packet without a typed header
OnIntercept(In["WiredMovements"], e => {
    var p = e.Packet; int n = p.ReadInt();
    for (int i = 0; i < n; i++) { /* read fields in wire order */ }
});

// modify room settings (must own the room)
ModifyRoomSettings(s => { s.Access = RoomAccess.Password; s.Password = "secret"; });
```

