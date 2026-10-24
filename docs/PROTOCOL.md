# Protocol

## Optional UU desktop access (Android 1.6.6)

This adapter opens the installed `com.netease.uuremote` app (4.42.0 or newer). It does not route video through
the relay and sends no UU identifiers, accounts or bindings to the computer or relay. Windows/macOS targets
must be selected by the owner in UU; Linux, Android and iOS targets are outside the external protocol.
The reference contract is <https://github.com/netease-uuyc/uuremote-integration-guide>.

`ComputerIdentity` matches an exact saved address or an explicitly selected re-pairing record, never a name.
Each saved Android computer receives a random persistent `id`. A confirmed line change retains it; an address
collision keeps the existing destination record and removes the discarded source record's desktop binding.
Ordinary re-pairing preserves an existing record; a new address without an explicit selection creates a new one.

`DesktopStore` encrypts each opaque binding with Android Keystore AES-GCM and the record ID as associated data.
Backup is disabled. Removing a computer removes its binding and any pending request for it. Device identifiers
and callback URLs must not be logged, displayed or copied. Native screens alone initiate remote control.

`UuRemote` serializes binding and PiP requests, persisting the record, operation, random 32-hex request ID and
creation time before launching UU. Binding passes both `nonce` and a `request` query parameter in the callback;
PiP uses the preserved `request` parameter. `DesktopProtocol` validates exact scheme, authority and path,
request identity, a 15-minute maximum age, duplicate query keys and mutually exclusive success/error fields.
Pending state is consumed before applying a valid result. Unknown/malformed callbacks cannot alter bindings.
Fifteen minutes is only a local acceptance limit, not evidence of device failure: some UU dialogs wait for the user.
The user can end waiting without disconnecting UU. Late callbacks are then ignored. A cold callback restores the
corresponding computer management page; a warm callback leaves the current screen intact and shows a short result.

Callbacks: `remoteai-uu://result/bind?request=<id>` and `remoteai-uu://result/control?request=<id>`.
Full screen requests have no callback and are reported only as handed to UU. PiP success confirms its initial
start, never ongoing connectivity. `device_not_found` invalidates the binding. Unsupported PiP offers full screen
through the existing menu rather than launching another connection automatically. External links never perform
remote control or pairing without a native user action.

The native menu can copy `remoteai://computer?id=<saved-record-id>` for another app on the same phone.
Only a single `id` parameter and a known local record are accepted. The link opens computer management, does not
log in or execute anything, and contains neither credentials nor UU identifiers. Other phones cannot resolve it.
The old `remotecli://connect` QR endpoint is dispatched separately and now checks its exact host.

Compatibility: the feature works with the existing 1.6.5 computer and relay. The Windows 1.6.6 bundle is released
alongside Android for version consistency; updating a running terminal host is not required for desktop access.

Three parties: the **viewer** (the phone app's pages), the **relay** (`relay/server.py`) and the **computer**
(`agent-windows/`). The viewer and the computer both make requests to the relay; the relay never connects to
either and never runs a command. Anything that speaks this protocol can replace one of the three: another
client, or a computer-side program for macOS or Linux.

All bodies are JSON in UTF-8. Errors are `{"error": "text for the person"}` with status 400, 401, 403, 429 or 500.

## Signing in

| Request | Result |
| --- | --- |
| `POST /api/login` `{"password": "..."}` | `{"ok": true, "token": "..."}` and a cookie `rcli`. Six wrong passwords from one address, or forty in total, lock sign-in for 15 minutes (429) |
| `GET /api/session` | `{"signed_in": bool, "version": "x.y.z"}` |
| `POST /api/logout` | forgets the token |
| `POST /api/ticket` (signed in) | `{"ticket": "..."}`: a sign-in that works once, within a minute, as `POST /api/login` `{"ticket": "..."}` |

Later requests carry the cookie or `Authorization: Bearer <token>`. A token is good for 90 days from its last
use. `POST` requests must have `Content-Type: application/json`; when an `Origin` header is present it must be
the relay itself. The computer signs in the same way as the viewer.

The app passes the password to its page once as `/#p=<password>`; the page signs in and removes it.
The program on the computer opens its own window on `/?open=<terminal>#t=<ticket>` (or `/#t=<ticket>` for the list):
the page signs in with the ticket, so the password is never part of an address. A terminal shown there is the one
the phone shows; output goes to every reader and input is taken from any of them.
The code shown on the computer is `remotecli://connect?u=<address>&p=<password>&n=<computer name>`; the name is optional and lets a phone that knows several computers tell them apart (the same name with a new address replaces the old address).

## Viewer

### Overview

`GET /api/terminal` →

```json
{"device": {"online": true, "enabled": true, "workspaces": ["demo"], "tools": ["claude", "codex", "opencode", "qoder", "cline", "shell"],
            "projects": [{"name": "demo", "path": "D:\\demo", "fixed": true, "exists": true, "tools": ["codex"], "kinds": ["Codex 桌面版", "终端"]}],
            "candidates": [{"name": "notes", "path": "D:\\notes", "updated": 1700000000000, "live": false, "tools": ["claude"], "kinds": ["Claude 桌面版", "终端"]}]},
 "terminals": [{"id": "...", "tool": "shell", "dir": "demo", "title": "PowerShell", "state": "running", "status": "idle", "seq": 12, "cols": 80, "rows": 24, "session": ""}],
 "sessions": [{"id": "<uuid>", "tool": "claude", "dir": "demo", "folder": "", "kind": "终端", "title": "...", "updated": 1700000000000, "live": false, "now": false, "status": "",
               "pinned": false, "hidden": false, "terminal": ""}]}
```

`workspaces` are the project names a terminal may be started in. `candidates` are folders that are no projects and
have conversations of Claude Code or Codex, for a project to be made of (`project_add` with the `path`): at most 40,
the ones in use (`live`) first, then by when they were last used. `kinds` says by what a folder is used, in the
computer's own words for the phone to show and search: a desktop app (`Codex 桌面版`, `Claude 桌面版`), an editor
(`编辑器`), a terminal (`终端`); a computer of an earlier version says none. The Windows computer leaves out a folder
that only a desktop app uses while that app does not run. `projects` say the same of each project's folder (`tools`,
`kinds`; a closed app is not left out there), and each of the `sessions` says by what it is used in `kind`: by the
program that has it open, else by what started it. A command line that runs in an editor's own terminal is the
terminal's (`终端`), an editor's extension the editor's. The phone tells apart two: the terminal's word, and every
other (an app). `sessions` are conversations the tools saved on
the computer; `live` means a program on the computer has it open, `terminal` is set when one of the relay's
terminals already shows it. `state` is `starting`, `running` or `closed`; `status` is `busy`, `idle` or empty.

`origin` describes the user-facing host, independently of `host` (writer ownership and takeover protection).
Windows checks the nearest recognized ancestor, verifies parent start times, and supports Positron, VS Code
(including Insiders), RStudio/rsession, Cursor, Windsurf and JetBrains hosts. A more distant ancestor cannot replace
that host; Remote AI ancestry still protects its child processes. Codex's explicit `originator: codex-tui` denotes
a terminal even with `source: vscode`. When a detached daemon has lost the foreground ancestry, that pair only
identifies an editor terminal (`origin: 编辑器`, `kind: 终端`), not a specific editor. No other project's running
process is used to guess the host. The writer remains `host: shared` and cannot be terminated by a display hint.

A conversation belongs to a project, named in `dir`. One that a program has open in a folder that is no project is
listed too: its `dir` is empty and `folder` is the full path of that folder. It can be looked at, answered and told
things where its tool lets it be reached, and taken on by a terminal like any other: `start` with an empty `dir` and
its `session` (and `takeover` or `fork` as for a conversation of a project). That terminal runs in the
conversation's folder and belongs to no project: its own `dir` is empty and it says the same `folder`; which folder
is what the computer listed, never what the viewer sends. While the terminal runs, the computer goes on listing the
conversation (not `live` any more), so that the terminal keeps its name and marks. Nothing else is started where
there is no project: not a new terminal, and not a conversation the computer did not list there. Saved
conversations of folders that are no projects, which nothing has open, are not listed. Once the folder is made a
project (`project_add` with that path) the computer lists its conversations under that project.
An idle conversation still held by an application remains listed outside projects too, in the opened group.
This does not expose closed history from unlisted folders.

`now` says of a `live` conversation whether it is in use: at work, asking or kept waiting for the phone; or in a
window of its own (a command line, open for as long as its program runs); or used in the last ten minutes. A desktop
app keeps every conversation it has loaded held by a process, shown or not, so `live` alone does not say that
anybody is at it. One that is held and not in use has the `activity` `locked`: it is kept out of what is going on,
and can still be looked at and taken on. A computer that does not send `now` is taken to mean that every held
conversation is in use. The phone uses distinct presentation groups: confirmation requests (`confirm`), executing
tasks (`busy`, or a terminal in `starting`), and opened but waiting tasks. Every other live session, including a
retained lock or `now: false` session, remains in opened with its actual status label. Closed records belong in
recent/history. A live pinned session stays in its state group, sorted first; only closed pinned records use the
separate pinned group. These presentation groups do not change `activity`, ownership or action capabilities.

`pinned` and `hidden` are what the owner said of a conversation from a phone (see "A conversation that is open on
the computer", the action `mark`). The computer keeps them, so every phone and every line shows the same; they are
a matter for the lists only, which put a pinned conversation first and leave a hidden one that is not open out
unless it is searched for. A computer that keeps them has the `marks` capability. At most 120 conversations are
listed, those that are open or pinned first, then the ones used last.

A live conversation whose tool tells of it through its hooks (the `reach` capability, see "A conversation that is
open on the computer") carries more: `status` may also be `confirm` (it asks for permission) or `waiting` (it has
stopped and is kept waiting for a message from the phone); `reach` lists what the phone can do to it now, of `say`
and `answer`; `ask` is the request it waits on, `{"id", "text"}`, or null; `last` is what it said last;
`wake` says that a message is handed to it where it sits idle (see there) instead of waiting for its next stop. Where the
tool tells nothing, `reach` and `ask` are null and `last` is empty. Every live conversation also carries `ways`
and `why` (see "The ways into a conversation, and stopping one"): by which way each thing is done to it, and the
computer's words for what cannot be done. `activity` is `active` for a conversation in a
command-line window of its own or one with `reach`, `locked` for another that a program holds, `history` otherwise.

Agents may advertise `info.features`: `codex-fork`, `codex-takeover`, `terminal-exit`, `files`, `update`, `peek`, `settings`, `work` and `chains`
(the last two: "The owner's work", below). These are forwarded
under `device.features`. Sessions may include `can_takeover`, `ownership_known`, and `takeover_reason`.
An agent verifies Codex's actual writer lock and process ownership before advertising takeover, then checks
again before terminating the selected independent CLI. Shared app servers and phone descendants are protected.
Process identifiers and command lines are never sent to the viewer. A closed terminal may include `exit_code`;
a nonzero value produces a visible error while preserving the output.

Each terminal also says what it is doing:

| `phase` | Meaning |
| --- | --- |
| `starting` | not running yet |
| `busy` | the program is working: it says so itself (Claude Code), or it has written output in the last 4 seconds |
| `confirm` | it has been quiet for 1.5 seconds with a yes/no question at the end of its screen. This is read from the text on the screen and can be wrong |
| `idle` | it waits for the next message; `done: true` when it worked before that |
| `ended`, `failed` | it has ended; `failed` with an exit code other than 0 |

`asks` is given for a terminal whose phase is `confirm`: up to 14 lines from the bottom of its screen, the question
it waits on, so that a viewer can show it where it offers to answer (`input` with `\r` for yes, `\x1b` for no).

`device.shell` is what the computer calls its plain terminal ("PowerShell", "bash"); a terminal of the tool `shell`
is titled with it. It is empty from a computer that does not say.

`said` is one line of what a running terminal's program last said or did, read from the screen the relay keeps for it; it is empty for a terminal that is not running and is only part of the list of all terminals.

`phase_at` is when the phase last changed (milliseconds since 1970). A viewer can keep the `phase_at` it last showed for a terminal to tell "finished, not looked at yet" from "waiting".

### Output

Session records carry optional `host`: `cli` is a verified standalone CLI, `shared` is a Codex desktop/editor backend retaining the writer lock, `remote` is a CLI under another terminal host, and `unknown` means the lock is held without verified ownership. `live` only describes a held writer/session; it does not prove that a visible window displays the conversation. Shared/unknown/other-remote sessions appear separately from active CLI terminals. An absent `host` on old agents must not be interpreted as a visible Codex window. Takeover rechecks ownership and refuses other terminal hosts as well as shared backends.

- `GET /api/terminal/ws?terminal=<id>&after=<seq>`: preferred WebSocket transport. Upgrade with
  version 13, a valid `Sec-WebSocket-Key`, the login cookie (or bearer token), and an `Origin` matching
  the relay. Output messages have `t: "out"` plus the same fields below. A state message arrives
  immediately, followed by output as it arrives and idle heartbeats. Ping frames keep an idle connection
  alive. Reconnect from the last `after` if the connection closes; connections renew after about an hour.
- `GET /api/terminal?terminal=<id>&after=<seq>&wait=<seconds>`: output after sequence number `after`. With
  `wait` (at most 25) the answer is held until there is something new.
- `GET /api/terminal/stream?terminal=<id>&after=<seq>`: one response that stays open (`text/event-stream`).
  Each line `data: {...}` is one piece; a line without output is sent when the state changes and every 15
  seconds. The relay ends the response after about 55 seconds; ask again with the latest `after`.

All three give

```json
{"device": {"online": true, "enabled": true}, "terminal": {...}, "chunks": [{"seq": 13, "data": "..."}], "reset": false, "after": 13}
```

`data` is what the program wrote to its terminal, escape sequences included. Sequence numbers are consecutive.
`reset: true` means the answer does not continue from `after`: clear the screen and continue from what is returned.
That is the case when earlier output is no longer kept, and when a reader with nothing (`after=0`) asks for a
Claude Code or Codex terminal that draws on the alternate screen: it is given the last output only (about 120,000
characters). In both cases the first piece begins with the terminal modes in force at that point (alternate screen,
mouse reporting, bracketed paste and the like), so the reader's terminal is in the state the program expects. A
fullscreen program draws only what changed; a reader that began this way asks it for a full picture by changing the
terminal's size and changing it back.
A reply to `GET` is gzip-compressed when the request accepts it. API answers are sent with `no-store`. The pages
are served with an `ETag` and `Cache-Control: no-cache`; an HTML page names its scripts, styles and fonts with a
mark of their content (`app.js?v=<12 hex>`), and a file asked for by its current mark is sent with
`Cache-Control: public, max-age=31536000, immutable`.
The WebSocket answers an offer of `permessage-deflate` (RFC 7692) with `client_no_context_takeover`: the relay's
messages are compressed as one stream for the whole connection, each of the reader's on its own. A reader that
does not offer it is sent plain text.
One answer carries at most about 180,000 characters; `after < terminal.seq` means more is waiting.

### Operations

`POST /api/terminal` `{"action": "...", "id": "<16 to 32 hex digits>", ...}` → `{"id", "terminal", "state", "error"}`.
`id` is chosen by the caller; sending the same operation again returns its current state instead of doing it
twice. `state` is `queued` until the computer has done it, then `done` or `error`.

On an upgraded viewer connection, send the same operation object as a text message (`input`, `resize`,
`rename`, `close`, and for a chat `say`, `stop` and `answer`, for the connection's terminal). The relay replies with
`{"t": "ack", "status": 200, "id": "...", "terminal": "...", "state": "queued", "error": ""}`.
Validation errors return an acknowledgment with status 400; storage failures return 500. A queued
acknowledgment means accepted by the relay, not yet executed by the computer. Inputs can be sent in
order without waiting for each acknowledgment. If an acknowledgment is lost, retry the identical
operation ID and payload over either transport. Text messages are limited to 128,000 UTF-8 bytes.
Cross-origin upgrades and operations for another terminal are refused. Logout invalidates input on an
open connection immediately and stops its output on the next update or heartbeat.

The viewer falls back to HTTP if WebSocket is unavailable. Quick tunnels skip SSE because their edge
buffers it. On other hosts, an SSE connection without its first event within four seconds falls back
to held requests. Switching to the background cancels the active reader; returning resumes from `after`.

| `action` | Fields |
| --- | --- |
| `start` | `tool` (`claude`, `codex`, `opencode`, `qoder`, `cline`, `shell`), `dir` (a workspace name); optional `session` (continue that conversation), `takeover` (end the owning computer CLI first), `fork` (Codex only: new conversation with the source history), `history` (let the tool show its own list), `form: "chat"` (a conversation run through its tool's structured mode, see "Chats"; `claude` and `codex` only, needs the `chat` capability, not with `history` or `fork`) with optional `text` (the first thing to say, at most 8,000 characters) |
| `input` | `terminal`, `data` (at most 16,000 characters) |
| `say` | `terminal`, `text` (at most 8,000 characters, no control characters but line breaks and tabs). A message for a running terminal from where the terminal is not on the screen (a list, the app's workbench): what the box of the terminal's page sends, the text and then Enter. The relay knows from the program's output whether it takes pasted text in brackets (`ESC [ ? 2004 h`): it then gives the computer `input` with the text between `ESC [ 200 ~` and `ESC [ 201 ~` and `\r`, and otherwise the text on one line and `\r`. Refused while the terminal's phase is `confirm`: Enter would answer its question |
| `resize` | `terminal`, `cols` (20–240), `rows` (6–100). Not for a chat |
| `stop` | `terminal` of a chat: stop what it is at now (see "Chats"). Refused while its phase is not `busy` or `confirm` |
| `answer` | `terminal` of a chat, `ask` (the id of what it asks), `decision`: `allow`, `always`, `deny`, or `choice:<n>` for a question with choices |
| `rename` | `terminal`, `title` |
| `close` | `terminal` |
| `project_add` | `path`, optional `name`, `create` |
| `project_rename` | `name`, `to` |
| `project_remove` | `name`. The folder and what is in it stay. A project that stands in the computer's own settings (`fixed`) is renamed or removed from the phone only where the computer says it does that (the `projects` capability: the Windows program, for the projects added in its window); else the relay refuses |
| `update` | none. The program on the computer looks for a newer release of itself and installs it; needs the `update` capability |

Linux Python agents also offer OpenCode, Qoder and Cline when installed; Windows continues to offer Claude Code, Codex and shell. Conversation IDs are validated by tool: UUID for Claude Code/Codex/Qoder, `ses_` plus 10–40 alphanumeric characters for OpenCode, and 10–16 digits plus `_` and 4–10 alphanumeric characters for Cline. The same shapes apply to conversation reads and marks, terminal resumes and agent reports. The relay resolves the tool and workspace from its reported session list, not from client-supplied values.

Linux `RemoteMaxMode=read` starts Qoder with `--permission-mode plan` and Cline with `--plan`; Qoder `edit` uses `--permission-mode accept_edits`. OpenCode uses its own default permissions for `read` and `--auto` for `edit`; it does not provide a separate read-only flag. These modes remain the tools' own interactive permission modes. Qoder startup parameters follow its [CLI reference](https://docs.qoder.com/cli/cli-reference) and [Plan mode](https://docs.qoder.com/cli/plan-mode) documentation.

### Files

`POST /api/files` `{"id": "<16 to 32 hex digits>", "action": "file_list" | "file_read", "dir": "<workspace>", "path": "sub/folder", "offset": 0}`
is held until the computer has answered, at most 25 seconds; it needs the `files` capability. `path` is relative
to the project folder, with `/`.

- `file_list` → `{"path", "entries": [{"name", "dir", "size", "modified" (ms), "hidden"}], "more"}`; at most 3,000 entries.
- `file_read` → `{"path", "size", "modified", "offset", "data" (base64), "end"}`; one piece is at most 737,280 bytes,
  the next is asked for with `offset` advanced by the bytes received.

A refusal is `400 {"error": "..."}`. The computer answers only for paths inside the project folder and refuses a
path whose real location, after links and junctions, is outside it. The relay keeps nothing of an answer.

Uploads use the same authenticated endpoint and require both `files` and `file-upload` capabilities (currently
Windows). Every request has a fresh operation `id`, the fixed workspace `dir`, destination `path` including the
filename, and a client-generated 32-character lowercase hex `upload` ID. Only these operations are accepted:

- `file_upload_start` adds integer `size` (0 through 314,572,800 bytes), returning `{"offset": 0}`.
- `file_upload_chunk` adds integer `offset` and base64 `data` (1 through 737,280 decoded bytes). The offset must
  equal the bytes already written; the reply's `offset` is the new byte count. Chunks cannot exceed the declared size.
- `file_upload_finish` publishes the complete file, returning `{"offset": <size>}`. It refuses incomplete files.
- `file_upload_cancel` removes this upload's temporary file. Cancelling an unknown ID is harmless.

The Windows agent owns at most four uploads per instance. Each holds its destination directory and ancestors
against rename, rejects reparse-point subdirectories, absolute/traversal paths, alternate streams and reserved
Windows names. It writes a temporary file in the destination directory and atomically renames it after flushing.
Existing files and folders are never replaced, including a name created while uploading. Cancellation and sessions
idle for ten minutes remove temporary files; forcibly terminating the computer process can leave hidden
`.remoteai-upload-*.tmp` files that may be removed when no upload is active. Uploads are not resumable after restart.
The relay validates size, offset and base64 bounds and forwards only operation-specific fields; it does not persist
file contents. An older relay or agent refuses upload operations without affecting file reads.

Android implements `WebChromeClient.onShowFileChooser` with `ACTION_OPEN_DOCUMENT`, `CATEGORY_OPENABLE`, local
files and optional multiple selection. Only granted `content:` URIs from external providers are returned to the
requesting WebView; cancellation, replacement of the page or Activity destruction clears the callback. No broad
storage permission is requested. See the [Android file chooser API](https://developer.android.com/reference/android/webkit/WebChromeClient#onShowFileChooser(android.webkit.WebView,%20android.webkit.ValueCallback%3Candroid.net.Uri[]%3E,%20android.webkit.WebChromeClient.FileChooserParams)).
Downloads continue using `file_read` and the native Saver bridge, with size, offset and modification-time checks
across chunks. The browser fallback uses an ordinary download; the Android app saves through its existing Saver.

### A conversation that is open on the computer

`POST /api/conversation` `{"id": "<16 to 32 hex digits>", "session": "<uuid>"}` is held like `/api/files` and needs the
`peek` capability. It gives the last things said in a conversation the computer listed, without touching the program
that has it open:

```json
{"title": "...", "updated": 1700000000000, "more": false,
 "messages": [{"role": "user", "text": "..."}, {"role": "thought", "text": "..."}, {"role": "tool", "text": "Bash：run the tests", "detail": "npm test", "result": "12 passed"}, {"role": "assistant", "text": "..."}]}
```

The last 40 things the user and the tool said to each other, with complete text up to 131,072 UTF-16 code units per message, oldest first, with what
the tool did between them (`tool`; of one unbroken run of those the last 30, and a line that says how many came
before); `more` says that earlier ones exist. Total text is bounded to 262,144 UTF-16 code units by dropping older entries. Exceptionally large blocks carry `truncated: true`, shown explicitly on the phone. `tool` names
something the tool did. What a tool writes into a conversation for itself (reminders, results of commands) is left
out. The tool and the folder asked of the computer are the ones it reported for that conversation.

With `"action": "say"` or `"action": "answer"` the same request acts on a conversation that has that way in its
`reach` (the computer needs the `reach` capability). Neither takes the conversation from the program that has it:
it stays open on the computer and is used there as before.

The Windows agent sets both tools' `SessionEnd` command hooks to `3` seconds, within
[Codex's supported limit](https://learn.chatgpt.com/docs/hooks) and
[Claude Code's shutdown budget](https://code.claude.com/docs/en/hooks#sessionend).
Hooks that only report state use `10` seconds; only `PermissionRequest` and `Stop` retain `2000` seconds for
phone interaction. Checking the installed hooks replaces Remote AI's previous timeouts, preserving other
hooks and the configuration backup. Codex may require reviewing the changed definitions in `/hooks` again.

- `messages[].role` is `user`, `assistant`, `tool` (one step of the tool's: for Claude Code its name and what it is
  for, or else its command or file; for Codex the command that ran, or the files read or changed, as Codex notes
  them for its own window; with `detail`, the command itself where the step is told by something else, and
  `result`, the first lines of what came back, six at most, where there are any) or `thought` (what the tool said of its work between its steps: the summary Claude Code
  keeps of its thinking, where it keeps one, and Codex's of its reasoning). A page that knows no `thought` shows it
  as it shows `assistant`.
- With `"action": "end"` (feature `end`) the computer ends the program that has the conversation: it stops at
  once, whatever it was at, and is open no more; its record stays. Only for one listed as `live`; the computer
  refuses, in words for the person, where it does not know the conversation's program for certain
  (`can_takeover`). The answer is `{}`.
- What a `read` answers carries `asked`, the time (ms) its user said the last thing, 0 where the record does not
  say: a conversation at work has been at it since then.
- Without an action (or with `"read"`) the answer is what was last said in it: `{"messages": [...], "more": false,
  "title": "...", "stamp": "<the computer's word for how its file was>", "status": "busy"}`. A page that follows a
  conversation asks again every moment and says what it has, `"since": "<that stamp>"`: the computer then answers
  `{"same": true, "stamp": "...", "status": "..."}` unless the file has changed. `status` is how the conversation
  stands now, as in the list. A computer of an earlier version gives no stamp and answers in full every time.
- `{"action": "answer", "ask": "<id of the request>", "allow": true|false}` answers the request for permission the
  conversation waits on. → `{}`. A request that was answered meanwhile, on the computer or the phone, is refused.
- `{"action": "say", "text": "<1 to 8000 characters>"}` leaves a message for it. → `{"now": true}` when the tool
  takes it at once (it had stopped and was waiting for it), `{"now": false}` when it will take it once it has done
  what it is doing. A conversation that has stopped without waiting can be told only where it says `wake`: one that
  sits idle in the program that has it, and whose tool has a way of its own to hand it a message there. For Codex
  the computer runs `codex queue --thread <id> --message <text>`: the conversation takes the message as what its
  user says next. For Claude Code the computer uses Claude Code's own cross-session messages, sent through a Claude
  Code session started for that one message (one tool, `SendMessage`; no hooks; no conversation kept; whether the
  message was carried is read from what that tool answered, and a session that did not call it is started again, three
  times at most) and addressed
  to where Claude Code notes that the conversation takes such messages. A conversation takes such a message as
  another session's, not as its user's, so the user's words are not sent that way where it can be avoided: while
  Claude Code runs this program's hooks, the words are left for the conversation, the message only wakes it (it
  carries none of the user's words), and the conversation's own Stop hook hands
  it the words when that turn ends, as its user's next instruction. Where the words are not taken within 40 seconds,
  or the hooks are not installed, they are sent as the cross-session message itself, framed as the user's words from
  the phone. Either way takes some seconds, so the relay holds the request for up to 85 seconds.
  → `{"now": true, "woke": true}`. In what `read` gives of such a conversation the user's words stand as what the
  user said; the message that woke it and the answer to that are left out. Either way the conversation is not
  taken from its program. Without `wake` (a Codex without the command, a Claude Code that notes no such address) an
  idle conversation says that it cannot be told.

- `{"action": "delete"}` puts a saved conversation of a project away. → `{}`. It needs the `delete` capability instead
  of `reach`, and is refused for a conversation that a program has open or a terminal runs. The computer is asked with
  the operation `session_delete` and destroys nothing: a conversation of Claude Code's goes into the recycle bin (on
  Linux the user's trash), with the folder that belongs to it; one of Codex's is archived by Codex itself
  (`codex archive <id>`; `codex unarchive <id>` brings it back). The overview no longer lists it from then on.
- `{"action": "mark", "pinned": true|false, "hidden": true|false}` (one of the two, or both) says of any listed
  conversation, open or not, that it is pinned or hidden, or no longer is. → `{}`. It needs the `marks` capability
  instead of `reach`. The computer is asked with the operation `session_mark` and keeps the marks by tool and
  conversation in its data folder (`conversation-marks.json`, at most 300 of each kind); the overview says the new
  state at once.

What a Stop hook answers to hand its tool the phone's words hangs on the tool: Claude Code from 2.1.283 on is given
them as added context (`hookSpecificOutput.additionalContext`), with which the conversation goes on and which its
screen shows as feedback; an earlier Claude Code, and Codex, are told by a blocked stop (`decision: "block"`, the words
as the `reason`), which Claude Code shows as an error of the hook although nothing failed. Either way the words stand
in what `read` gives as what the user said.

How this reaches the tools: Claude Code and Codex run hooks of the computer's program (it puts them into the tools'
own settings when its owner switches that on). A hook for a request for permission is answered with the phone's
answer; a hook that runs as the tool stops is answered with the message, as something to go on with. Claude Code
shows its question on the computer while the hook runs and takes whichever answer comes first. Codex asks its hooks
first and shows a running hook meanwhile, so its request is kept for the phone only while nobody has touched the
computer's keyboard or mouse for a minute: it is let go the moment somebody does; while it is kept, the program
says at the corner of the computer's screen what waits and how to have it there. A request of either tool that
nobody has answered after half an hour, with still nobody at the computer, is not left at the tool's own question,
which only somebody at the computer could answer: the hook answers that it is not allowed for now, and the tool
stops and says what it needs. The conversation is then one that has stopped, to be told from the phone (`say`)
whenever its owner reads it; it asks again, and that request is the phone's to answer. A request that is kept
outlives the program being started again (an update): its hook waits up to a minute and a half for the program to
listen again and hands the request over anew. Where
the program's owner switched that off (the setting `ask`, below), the hook ends at once, Codex shows its own
question, and the conversation is listed as asking, without `answer` among what the phone can do. A tool that
stops is not kept waiting unless the program's owner chose a time for that (the setting `wait`, below): then, while
nobody is at the computer, it waits that long for a message, which it takes at once, and the tool shows its hook as
running all that while. A conversation that has stopped is idle, and is told where it says `wake` (see `say` above).
Nothing is kept for a conversation that runs in one of the program's own terminals (its hook was started, however
many steps down, by the program, or tells of the conversation a terminal runs): the phone is at that terminal, where
the tool shows its own question and takes what is typed.

### The program itself

`POST /api/computer` `{"id": "<16 to 32 hex digits>", "action": "..."}` is held like `/api/files` and needs the
`settings` capability, which the Windows program has. It is for a phone that is away from the computer: the program
tells a few of its settings and the lines it can be reached by, changes one of those settings, and turns to another
line. The program answers and decides everything; the relay checks the shape of the question and keeps nothing.

| `action` | Fields | Answer |
| --- | --- | --- |
| `settings_read` | none | what the program tells about itself, below |
| `settings_change` | `name`, `value` | the same, after the change. `rights`: `full`, `edit` or `read` (the mode Claude Code and Codex are started in from the phone); `tunnel`: `auto` or `http2` (used by the next tunnel); `autostart`, `update`, `reach`, `ask`: `true` or `false` (`reach`: the program's hooks are in the settings of Claude Code and Codex, see "A conversation that is open on the computer"; `ask`: a request for permission of Codex's is kept for the phone while nobody is at the computer, `true` unless switched off); `wait`: `"0"`, `"60"`, `"300"`, `"900"` or `"1800"`, the seconds a tool that stops is kept waiting for the phone's message while nobody is at the computer, as a word (`"0"`, not at all, unless chosen) |
| `line_switch` | `to`: the `id` of a line | the same; `change.state` is now `preparing` |
| `line_take` | none | `{"address", "ticket", "key", "seconds"}`, when `change.state` is `ready` |
| `line_keep` | `key` | the same as `settings_read`; sent through the new line |

```json
{"name": "my-computer", "version": "1.2.0",
 "lines": [{"id": "lan", "kind": "lan", "current": false, "ready": true},
           {"id": "cloud", "kind": "cloud", "current": true, "ready": true},
           {"id": "r3f9a0c1d2e4b", "kind": "own", "name": "home", "ms": 42, "current": false, "ready": true}],
 "settings": {"rights": "full", "tunnel": "auto", "autostart": true, "update": false, "reach": true, "ask": true, "wait": "0"},
 "reach": [{"tool": "claude", "name": "Claude Code", "state": "good"}],
 "change": {"state": "", "to": "", "note": "", "left": 0}}
```

`reach` says, while that setting is on, how each tool that is used on the computer stands: `good` (its hooks are the
program's and it has told of a conversation through them), `ready` (they are, and it has yet to) or `failed` (they
could not be written).

`kind` is `lan`, `cloud` (the tunnel; `ready: false` when the tunnel program has to be fetched first), `own` or
`public` (relays, with `name` and `ms`, how long the relay took to answer the computer: -1 none, -2 not asked yet).
A relay's `id` is a word made from a hash: no address of a relay is told. Relays are added, and passwords and the
port changed, at the computer only.

Turning to another line is done so that a phone far from the computer is not shut out by a line it cannot reach:

1. `line_switch`: the program makes the new line ready beside the one in use (checks the relay, or starts its own
   relay and the tunnel) and says how far it is in `change`: `state` `preparing` with a `note`, then `ready`, or
   `failed` with why. Nothing of the line in use is touched. A relay that does not know `/api/computer` is not turned
   to, since the phone could not say there that it arrived.
2. `line_take`, within 45 seconds: the answer is the new line's `address`, a `ticket` that signs in there once (see
   "Signing in"; the password of the new line does not travel through the old one) and a `key`. The agent then
   reports to the new line; the old one is left as it was.
3. The viewer opens `<address>/computer/#t=<ticket>&keep=<key>`. That page signs in with the ticket and sends
   `line_keep` with the key. The program then keeps the line: it is the one it starts with from now on, and what the
   old line needed is ended. `change.state` is `trial` in between, with `left`, the seconds remaining.
4. Without `line_keep` within `seconds` (120), the program goes back to the line it had, and `change` says `failed`
   with why, for five minutes.

Inside the app the page calls `RemoteCliNative.moved(address, ticket, key)` instead of leaving, and
`RemoteCliNative.kept()` after `line_keep` was answered: the app opens the page at the new address, and lists the
computer under that address once the line is kept. While a line is being changed from the phone, it is not changed
at the computer.

### The owner's work

The Android collaboration overview links stored rules to `/?tab=together&do=chain&item=<id>`
or `do=schedule&item=<id>`. The page waits for that list to load before opening its existing
rule; a missing id is reported and never treated as a request to create a new rule. Omitting
`item` opens the creation form. `do=handoff` and `do=many` open immediate actions.
Chains and schedules are loaded independently, with failures and explicit retry shown per
kind; a failed refresh retains previously read data. New-rule forms retain the same rule id
when a save is retried, preventing duplicate rules after a lost acknowledgement. Schedule
switches ignore repeated taps while saving; a requested refresh is queued if one is underway.

`POST /api/work` `{"id": "<16 to 32 hex digits>", "action": "..."}` is held like `/api/files` and needs the `work`
capability, which the Windows program and the Linux agent have. The three actions that begin with `chain` need
`chains` besides. The Windows program hears that a conversation has stopped from the tools' own hooks; the Linux
agent has none, and sees a finished turn by watching the source conversation's record grow and then stay quiet
for a little while. The computer keeps and does all of it; the relay checks the shape of the question, passes it
on as the operation `work_<action>` and keeps nothing.

| `action` | Fields | Answer |
| --- | --- | --- |
| `stats` | `days` (1 to 90, 14 without it) | the numbers of the last `days` days, below |
| `schedules` | none | `{"schedules": [...]}`: the questions set to be asked at a time |
| `schedule_set` | `item`: `id` (16 to 32 hex digits; a known one is set anew), `session`, `text` (1 to 2,000 characters), `kind`, and for `once` `at` (milliseconds since 1970, later than now, within a year), for `daily` `time` (`"09:30"`, the computer's own time), for `every` `minutes` (5 to 10,080); optional `on` | `{"schedule": {...}}` |
| `schedule_remove` | `schedule`: its `id` | `{}` |
| `chains` | none | `{"chains": [...]}`: what is passed on when a conversation stops |
| `chain_set` | `item`: `id`, `from`, `to` (two different conversations the computer lists), `text` (1 to 1,000 characters), `rounds` (1 to 10) | `{"chain": {...}}` |
| `chain_remove` | `chain`: its `id` | `{}` |
| `handoff` | `from`, `to` (two different conversations; `to` is open), optional `note` (at most 1,000 characters) | `{"status": "accepted", "note": "...", "length": n}`; held for up to 85 seconds, as a message for an idle conversation is |

The tool of each conversation named is the one the computer listed it with, whatever the phone says. A conversation
counts as open when a program on the computer has it (`live`) or one of the phone's own terminals runs it.

**The numbers** (`stats`) are read from the tools' own records on the computer (Claude Code's
`projects/*/*.jsonl`, Codex's `sessions/**/rollout-*.jsonl`), each record anew only when it has changed. Nothing that
was said leaves the computer: numbers, dates, and the names of folders.

```json
{"ready": true,
 "days": [{"date": "2026-10-13", "prompts": 11, "sessions": 3, "active": 2280, "in": 51200, "out": 23900, "cached": 1100000, "wait": 2.6}],
 "tools": [{"name": "claude", "prompts": 58, "active": 18000, "tokens": 310000}],
 "projects": [{"name": "web-shop", "prompts": 45, "active": 13920, "tokens": 250000}],
 "hours": [0, 0, 0, 0, 0, 0, 0, 0, 3, 9, 12, 8, 4, 6, 14, 9, 7, 5, 2, 1, 0, 0, 0, 0], "wait": 3.4, "updated": 1791892800000}
```

- `days`: one for each day, today the last, by the computer's own calendar. `prompts`: what the owner asked (not
  what a tool gave back, not a helper's own conversation). `sessions`: the conversations something was asked in.
  `active`: seconds the tools worked on it, from each prompt to the last thing of its turn, a turn counted for
  three hours at most (Codex notes the length of each turn itself). `in`, `out`, `cached`: tokens the tools note
  for their answers: fresh input, output, and input read from or written to the cache. `wait`: the middle value of
  the seconds from a prompt to the first thing that came back (null on a day with none; an answer later than ten
  minutes is not counted).
- `tools`, `projects`: the same summed over the whole span, the most worked on first (at most four and six). A
  project goes by its name where the conversation's folder is one or lies in one, else by the folder's own name.
- `hours`: prompts by the hour of the day, over the whole span. `wait`: the middle value over the whole span.

**A question asked at a time** as it is answered: what was set, with `tool`, `title` (the conversation's, as it was
then), `next` (milliseconds; 0 when nothing is due), `runs`, and `last` (`{"at", "ok", "note"}`, or null), the note
in words for the person ("已交给对话", or why not). `kind` is `once`, `daily` or `every`. The computer looks every
15 seconds for what is due, moves each on to its next time before it asks (a single one is switched off), and asks
only while remote access is allowed. At most 30 are kept.

**What is passed on** (`chains`) as it is answered: `from`, `to`, their tools and titles, `text`, `rounds`, `left`
(how often it may still run), `runs`, and `last`. When the conversation `from` has done what it was at and stopped
by itself (its tool's Stop hook says so; not when it was told to stop, and not while a message of the phone's waits
for it), every rule that begins there and has runs left runs once: `left` goes down by one, and `to` is told what
`from` said last (at most 3,000 characters) and the rule's words. Setting a rule again gives it its rounds anew. Two
rules, one each way, let two conversations go back and forth until their rounds are used up. At most 20 are kept.

**A conversation handed over** (`handoff`): the computer reads `from` as the conversation's window does and writes
`to` one message, in its owner's words: what was asked first and last (800 characters each), what was answered
last (2,000), the last eight steps (160 each), and the note.

**How a conversation is told** any of this: one that runs in a terminal of the phone's has it typed there, as the
operation `say` puts a message (pasted text, then Enter; not while its tool says that it asks a question). Any
other is told by the way that is there now, as the action `say` of `/api/conversation` is ("The ways into a
conversation", below). The Linux agent knows the first way only.

The computer keeps the questions and rules in `schedules.json` and `chains.json` of its data folder; the Windows
program encrypts their words for the Windows account.

## Chats

A chat is a terminal whose program is not drawn on a screen: the computer runs Claude Code or Codex through the
tool's own structured mode, and the terminal's output is the conversation's events, one JSON object a line. It is
what the phone shows as a conversation (`web/chat/`; the terminal page hands such a terminal over to it), begun by
`start` with `form: "chat"`. Everything a terminal has applies: numbered output, `after`, `reset`, several readers,
the list, `close`, `rename`, keeping it across an update of the computer's program. The computer offers it with the
capability `chat` (Windows program 1.7.0, Linux agent 0.7.0; the Rust agent does not).

| Tool | Run as | Said | Stopped | Asks |
| --- | --- | --- | --- | --- |
| Claude Code | `claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages --replay-user-messages --permission-prompt-tool stdio --session-id <new id>` (or `--resume <id>`), with `--permission-mode plan` or `acceptEdits` for the computer's `read` and `edit` | a `user` message on its standard input; one sent while it works is taken when it gets to it | the control request `interrupt` | a `can_use_tool` control request, answered `allow` (with the input), `allow` with one validated and displayed `updatedPermissions` suggestion for `always`, or `deny` |
| Codex | `codex app-server`; `thread/start` with the project folder (`sandbox` for `read` and `edit`) or `thread/resume` | `turn/start`, or `turn/steer` while a turn runs | `turn/interrupt` of the running turn | `item/commandExecution/requestApproval` and `item/fileChange/requestApproval`, answered `accept`, `acceptForSession`, `decline`; `item/tool/requestUserInput` with one question and its choices |

Run on Claude Code 2.1.283 and Codex 0.163.0. The conversation is the tool's own and saved where its other
windows find it: it is listed with the others, and can be gone on with in a terminal or a desktop app once the chat
is closed. While a chat has it, the computer lists it as held by the phone's own program. Requests of the tools that
this protocol does not answer are refused, so that the tool does not wait.

Claude permission suggestions are validated by the computer before offering `always`, both for chats
(Windows and Python Linux) and Windows `PermissionRequest` hooks. Only a complete `addRules` / `allow`
suggestion for `localSettings` is accepted; chats also accept `session`. Changes to user/shared settings,
permission modes, directories and replacement rules are not offered. Each suggestion contains 1–4 rules
and its complete scope must fit within 600 characters. For `Bash` and `PowerShell`, a nonempty command
is required: either no `*`, or one trailing ` *` / `:*`. Embedded stars, including quoted file globs
and regex stars, multiple stars and multiline commands cannot be saved from the phone. This deliberately
also excludes some otherwise valid patterns such as `git log * main`. A rejected suggestion leaves
single-use allow and deny available; a forged `always` answer is rejected. If several suggestions arrive,
only the first eligible one is displayed and returned unchanged, never the entire suggestion list.
The program does not rewrite or broaden rejected rules, edit existing permission lists or suppress
Claude's startup warnings. See Claude Code's [permission updates](https://code.claude.com/docs/en/hooks#permission-update-entries)
and [wildcard matching](https://code.claude.com/docs/en/permissions#wildcard-patterns).

The events (`t` names the kind; `at` is the computer's time in milliseconds):

| `t` | Fields | Means |
| --- | --- | --- |
| `state` | `state`: `starting`, `busy`, `confirm`, `idle`, `ended`; `at` | how it stands; `busy` from what was said until the turn ends |
| `session` | `session` | the conversation's id in its tool, once known |
| `history` | `messages` (as `/api/conversation` gives them: `role`, `text`, `detail`, `result`), `more` | what was said before, where a saved conversation is gone on with; it comes before what follows |
| `item` | `id`, `kind` (`user`, `text`, `thought`, `step`, `note`), `text`, `at`; a `user` item may have `op` (the phone's operation id it came of); a `step` has `name`, `detail` (the command, or the file), `state: "run"` | something said or done begins |
| `more` | `id`, `text` | more of a text or thought, as it is written |
| `set` | `id`, `text` | the whole text, once its tool says it; replaces what came in pieces |
| `out` | `id`, `text` | more of what a step prints; at most 60,000 characters a step |
| `done` | `id`, `ok`, optional `result` (its last lines, where nothing was printed as it ran) | a step ended |
| `ask` | `id` (16 hex), `kind` (`tool`, `plan`, `command`, `edit`, `question`), `title`, `text`, `options` (`id`, `label`) | it asks before it goes on; answered by `answer` |
| `answered` | `id`, `decision` (or `gone`: it no longer asks, after a stop or when it ended) | what it asked is settled |
| `turn` | `end` (`done`, `stopped`, `failed`), optional `error`, `at` | a turn ended |
| `note` | `text`, `at` | something said of the conversation (the computer's program was updated, the tool exited) |

A reader that begins later is given what the relay keeps (as for a terminal): an event about an item whose `item`
was before that is shown as a new one. The computer reports a chat in `terminals` with `form: "chat"`, `status`
(`starting`, `busy`, `confirm`, `idle`), `said` (one line of what it said last) and `ask` (what it asks, or ""); the
relay lists its phase from `status`, `said` from the computer and `asks` from `ask`, not from a screen.

## Computer

Android computer management reads `GET /api/terminal` and displays `device.version`, `newer`,
`online`, `enabled` and the `update` capability. An empty `newer` means no advertised update;
it does not prove the current version is the latest. After user confirmation it sends
`POST /api/terminal` with `{"action":"update","id":"<32 hex>"}`. This asks the computer to
check **and install**, not merely check. Retries while queued or after a lost response reuse
the same id. `done` only acknowledges invoking the updater; it is not an installation result.
The management page polls the overview every five seconds while visible, including after
reconnection, and confirms completion only from an online computer reporting a higher version.
After three minutes without confirmation it shows an inconclusive result rather than success.
Leaving the page stops polling; returning resumes the same pending request within this app instance.
Unsupported, disabled, offline and signed-out computers show guidance instead of an active update button.

`fork` requires a session UUID, the `codex-fork` capability, and `takeover=false`. The new terminal starts with
an empty session association; the source remains untouched. The agent only associates a new Codex conversation
when its verified writer belongs to that terminal's process tree, never by picking the first conversation in
the same folder. A locked source cannot be resumed concurrently; choose fork or first release its writer.

`POST /api/terminal/agent`, a few times a second while something happens and about once a second otherwise:

```json
{"info": {"instance": "<32 hex, new at each start>", "enabled": true, "tools": ["claude", "shell"], "workspaces": ["demo"],
          "features": ["files", "update", "reach", "marks"], "version": "0.7.0", "newer": "", "shell": "PowerShell", "projects": [...], "candidates": [...]},
 "terminals": [{"id": "...", "state": "running", "status": "idle", "tool": "shell", "dir": "demo", "created": 1700000000000, "title": ""}],
 "output": [{"terminal": "...", "seq": 13, "data": "..."}],
 "acks": [{"id": "<operation id>", "error": ""}],
 "sessions": [...]}
```

→ `{"operations": [{"id", "terminal", "action", ...}], "output_ack": {"<terminal>": 13}, "titles": {"<terminal>": "<name the owner gave>"}}`

The computer keeps each piece of output until `output_ack` covers its `seq`, and reports each finished
operation in `acks` until the relay stops sending it. It checks every operation itself: new tasks and file
operations require a project; control of an already open conversation uses its verified owner and discovered
working directory, including folders outside the project list. A conversation must exist on disk.

`POST /api/terminal/agent/pull` `{"instance": "...", "wait": 12}` is held by the relay until an operation is
waiting and returns it at once, so a key press does not wait for the next report.

An answer to `file_list`, `file_read`, `session_read` (the operation behind `/api/conversation`, with `session`,
`tool` and `dir`) or one of the operations of `/api/computer` travels in `acks` as `{"id", "error", "result": {...}}`.
It is handed over once and not kept: when a relay asks for the same operation again (the computer had turned to
another relay with the answer), the Windows program answers with an error, and the viewer asks anew.

### A computer that turns to another relay

The terminals belong to the computer, not to the relay that passed on the request to start them. `terminals` names
every terminal the computer has, and the relay keeps its list in step with it:

- A terminal the relay lists as running under this `instance` and that a report no longer names has ended; it is
  listed as ended. A report without `terminals` says nothing about them.
- A terminal the relay does not know is listed when the report says what it is: `tool`, `dir` and `created`
  (milliseconds since 1970), with `session`, `title` (a name the owner gave it), `cols`, `rows` and `exit_code`
  where they apply. A running one is always taken. An ended one (`state: "closed"`) is taken where it would be
  kept: not beside a terminal of the same conversation, and not when the list is full of newer ones. A computer
  that sends none of these fields is treated as before.
- `output_ack` then names the terminal with the last piece the relay has (`0` for one it just listed). A computer
  that kept the output it had given goes back to the piece after that one and sends it again, marked `"old": true`
  so that it does not count as the program working. When what it kept begins later than that, the first piece is
  marked `"restart": true` and begins with the terminal modes in force: the relay drops the output it kept before,
  and readers get `reset`.
- `titles` are the names given with `rename`; the computer reports them back as `title` to a relay that lists the
  terminal for the first time.

The Windows program does all of this, keeping about 600,000 characters of each running terminal and the last
screens of the twelve terminals that ended last. So the way of connecting can be changed between the local relay
and a relay of one's own while terminals run, and a relay that lost its list is given it again.

A conversation is listed once among the terminals: a running terminal replaces the ended ones that showed the same
conversation (`tool` and `session`), and of several ended ones the last that printed anything is kept.

The Windows program keeps its `instance` across a restart for an update: it notes its open terminals, and after
the restart reports the same instance and the same terminal ids, with output numbered on from where it was.
The relay and the viewer then see the same terminals continue.

## A relay for everyone

Started with `--public` (or `RCLI_PUBLIC=1`), a relay has no password of its own and serves many computers, each in
a space of its own:

| Request | Result |
| --- | --- |
| `GET /api/session` | `{"signed_in": false, "public": true, "key": bool}`; `key` says that a word is needed to have a space made. Nothing else about the relay is told outside a space, not its version either |
| `POST /api/space` `{"key": "..."}` | `{"space": "/c/<20 hex digits>", "password": "..."}`. The password is told once; the relay keeps its SHA-256. 403 without the right word (`RCLI_JOIN_KEY`), 429 after six spaces in a day from one address, 503 when the relay has as many as it allows |

Everything in this document then holds under the space's address: `https://relay/c/<id>/api/login`,
`https://relay/c/<id>/api/terminal`, the pages at `https://relay/c/<id>/`. That address is what the computer's
program uses as its relay and what the code for the phone carries. The sign-in cookie is set for the space's path,
so a phone can know several computers at one relay. The pages ask for `api/...` relative to where they are, which is
why they work at the top of a relay and in a space alike.

A space has its own sign-ins, tickets, terminals and operations; a token of one is not valid in another, and the
relay offers no list of spaces. A space whose files were not written for 30 days (nobody signed in, no terminal
changed) is removed. Outside the spaces such a relay answers only the two requests above.

The Windows program learns of relays for everyone from a published list, `public-relays.json` at the top of the
repository (`{"relays": [{"name", "url", "key"}]}`), read at start and every six hours; `REMOTECLI_RELAYS` names
another list. An entry may carry `hidden` in place of `url` and `key`: the JSON `{"url", "key"}`, each byte combined
(exclusive or) with the SHA-256 of the text `remote-cli public relays`, repeated, and written as base64 for URLs
without padding. It keeps an address out of plain sight in a public file and is no encryption; `tools/relay_list.py`
writes and reads it. It asks for a space once and keeps what it was given; when the relay no longer has the space, it asks
again.

## Desktop application adapter (Windows)

An agent advertises `<tool>-desktop-open` for each tool whose desktop application it can open a project in:

- `codex-desktop-open` when it finds the current user's installed `OpenAI.Codex` Windows x64 package
  (26.1007.2314.0 or later) and its desktop executable;
- `claude-desktop-open` when it finds the installed `Claude` Windows x64 package (2.31226.1.0 or later), the
  package declares the `claude:` scheme as its own, and its owner has not given that scheme to another program.

Installing/updating a desktop app requires restarting Remote AI to discover it.
This capability means opening a project/composer, not RPC control of running turns.
It does not depend on a CLI installation. Linux and older agents do not advertise it. The relay passes
on the names it knows (`DESKTOP_TOOLS`) and refuses `app_open` for a tool the computer did not name.

Use the existing authenticated `POST /api/terminal` operation queue:

```json
{"id":"0123456789abcdef0123456789abcdef","action":"app_open","tool":"codex","dir":"demo","draft":"Optional unsent text"}
```

Only these fields are allowed. `dir` is an existing Remote AI project name, not a caller-provided path.
Both relay and computer validate it; the computer resolves its allowed absolute directory and verifies
that it still exists. `draft` defaults to empty, accepts line breaks and tabs and is limited to 1000
UTF-16 code units. Remote control must be enabled. No terminal is created (`terminal` is empty).

The queued operation eventually returns `state: "done"` with a `result`:

```json
{"status":"handed_off","tool":"codex","sent":false,"message":"已交给 Codex 桌面应用。请在电脑上确认项目和草稿，内容尚未发送。"}
```

`handed_off` means the desktop executable accepted the launch request, not that the application
confirmed a project/thread was created. It may change the visible page/focus. The text is only prefilled;
the user sends it on the computer. There is no returned conversation ID or automatic inference.
`status: "unknown", sent: false` means an earlier attempt may have reached the app and is not replayed.
An error or timeout must not be displayed as successful creation or proof that nothing happened.

The draft is sent on the computer, and only then is there a conversation. The computer tells the phone which
one it is: a live conversation carries `opened`, the `id` of the `app_open` operation it came of, where it is
the first that began in that folder, in that application, after the application was opened for it (looked
for during 30 minutes; kept in memory only). The page that handed the draft over offers to enter it.

Each application is opened by a link of its own scheme, the entry it has for that itself
(`LinkedDesktopApp` in `DesktopAdapters.cs`; one class per application says where it is installed, what its
link is and how the link is given). Claude's is `claude://code/new?folder=...&q=...`: a new conversation of
Claude Code in that folder with the draft standing in its box, the link the app's own `ccd <folder>` command
builds (inspected in Claude Windows 2.31226.1.0). It is handed to Windows, which gives it to the application
that registered the scheme. The application refuses a folder on a network path, so the adapter does before
anything is reserved.

Codex's adapter invokes the installed desktop executable with a single quoted, percent-encoded
`codex://threads/new?path=...&prompt=...` argument. This route and its project-selection/creation behavior
were inspected in Codex Windows 26.1007.2314.0 (`bootstrap` and `main` packaged modules). This is a desktop
entry point, not an app-server API. The app handles its existing-instance dispatch. The adapter never
starts a separate app-server, edits the app database, removes locks or sends keyboard input.
Future desktop versions may change this entry point; launch success alone is deliberately not a native
UI completion check. This entry does not provide list/rename projects or close. Existing-conversation control
uses the separate capability below. A conversation already open and verified by the computer can be read, sent to
and stopped even when its folder is not in the phone's project list. Projects still control file browsing, new tasks
and project management. The computer uses the folder found during its scan; a path from the phone never grants
access to another folder.

Repeat the identical payload and `id` to query; using the same ID with different parameters is rejected.
The relay retains answers in its existing 10-minute operation cache. The computer reserves each operation
on disk before dispatch, then atomically replaces the reservation with its receipt. After a crash or lost
response it returns the receipt or `unknown` without dispatching again, including across relay restarts.
Records live in the computer data directory at `desktop-operations/<id>.json` and contain only a request
fingerprint and outcome, not the path or draft. They are retained with the computer's data; deleting them
removes replay protection for those IDs. A corrupt record fails closed. The phone disables duplicate clicks
and queries the same operation after a network failure; leaving the form does not cancel dispatch.

Checks: `tests/desktop_adapter_check.py` (isolated adapter/journal and real agent dispatch, both links),
`relay/test_relay.py` (capability per tool, validation, replay and timeout), `tests/desktop_ui_check.py` and
`tests/ways_ui_check.py` (real embedded page and relay, simulated app receipts). The tests do not create real
desktop conversations.

## The ways into a conversation, and stopping one

A conversation that a program on the computer has open is reached by the best way that is there, each tool by
what is its own:

| way | name | what it does | where |
| --- | --- | --- | --- |
| the application's own channel | `codex-desktop` | `say` while ready or working, `stop` of the one turn that is named | Codex's Windows desktop app, a supported version, a verified open conversation |
| the tool's hooks | `hook` | `answer` to a request for permission; `say` left for a conversation at work, handed over when it stops; `stop` before its next step | Claude Code and Codex in any program, for the hooks they run |
| the tool's own way to a conversation that sits idle | `wake` | `say`: the conversation is woken where it is | Claude Code by its messages between conversations (the desktop app's conversations too); Codex's command line by `codex queue`, never the desktop app's |
| closing its program | `process` | `end` | where the computer knows for certain which program has it (`can_takeover`) |

One place on the computer decides this (`Ways.cs`), for the list the phone is shown and for carrying out what
it asks, the same for every tool. For each thing the best way that is there is used, and the next where it is
not: Codex's desktop app takes a message through its channel while it is ready and while it works (it goes
into the turn that runs), but not while it waits for an answer to a question of its own; what is said then is
left for the conversation through its hooks, as for any other conversation at work. A live conversation is
listed with

```json
"ways": {"say": "hook", "stop": "codex-desktop", "answer": "", "end": ""},
"why": {"end": "这段对话由电脑上的应用管理，请在那里关闭它"}
```

`ways` names the way of each thing, `""` where there is none; `why` gives, for what cannot be done now, the
computer's words for the person (for `say` and `end` always, for `stop` while it works, for `answer` while it
asks). `reach`, `transport`, `turn` and `wake` say the same for a page or relay that knows one way for a
conversation only: `transport` is `codex-desktop` where the application's channel is there, else `hook` where
the hooks stop it. A relay passes `ways` on only for what it lets through in `reach`; one of an earlier
version passes neither on, and the page then takes the one way `transport` names.

The phone names the way it was told of (`transport` with `say` and `stop` for the application's channel, with
`stop` for the hooks); which way is used is decided on the computer as things stand then. Every `say` is
answered in one shape, whatever carried it: `{"status": "accepted", "sent": true, "now": <bool>, "way": ...}`,
`now` false when it was left for the conversation to take after what it is doing.

A request submitted with `transport: "codex-desktop"` retains its durable operation identity even if
its first dispatch selects a hook because the conversation became busy. Receipt lookup and payload
fingerprint checks precede route selection; retries never enqueue again or become a new native turn.
The accepted hook receipt confirms admission to the computer's outbox (see "Messages that wait"), which is
kept on disk: the message is handed over after an agent restart too, and the same `id` is answered by how
it stands.

The embedded reader stores native send recovery records in session storage, scoped by relay path,
computer instance, tool and session. Reopening after a reload restores records less than 24 hours old;
an interrupted in-flight request becomes unknown and is queried with `receipt: true`, never replayed
automatically. This does not preserve unsent drafts, hook/wake requests or guarantee recovery after
the tab or Android WebView is recreated.

What has no way: the answer of Codex's command line cannot be stopped from outside. Codex 0.162.1 refuses a
hook before a step that answers `continue: false` ("unsupported"), and the conversation of its window is not
one of its shared background service's, so no other client of that service can interrupt its turn. It is
ended (`end`), or taken into a terminal of the phone's.

`stop` with `transport: "hook"` names no turn: Claude Code runs a hook before each step it takes (`PreToolUse`),
and the computer answers the next one with `continue: false`. The turn ends there; the conversation and its
program stay. The answer to the request is `{"status": "asked", "transport": "hook"}` at once. A conversation
offers it (`stop` in `reach`) only while it is at work and has been heard before a step, which one opened
before that hook was in its settings is not.

The step need not run out first. Where it runs a command in a shell (Claude Code's `Bash` or `PowerShell`
tool, not one sent off with `run_in_background`), the computer ends that command as Esc would at the
keyboard: the shells that the conversation's own process started since the step began, with what runs below
them (`Running.cs`; the process is the one Claude Code notes for the session). Claude Code notes the step as
failed and comes to its next one, where it is stopped: 4.9 s from the request with a 90 s command running, on
Claude Code 2.1.283. What Claude Code started before the step, and what is no shell, is left alone. While it
only writes text there is no step and no hook: it stops when it next takes one, or not before its turn is over.

### Messages that wait

What the phone says to a conversation that cannot take it at once is kept by the computer, one item for each
message under the `id` of the request that carried it (`Outbox.cs`): in a file of its own, the words
encrypted for the Windows account and dropped as soon as the item is settled. A computer that keeps them
advertises `outbox`. Every `say` answers with `state` and `message` (the item's number) beside `status`,
`sent`, `now` and `way`; a `say` repeated under the same `id` is answered by how the item stands and sends
nothing again, also after the computer's program started anew. A number used for other words is refused.

| `state` | meaning |
| --- | --- |
| `queued` | it waits: the tool's hooks hand it over when they next run, or the conversation is woken for it |
| `paused` | stopped, failed, or interrupted delivery: it waits for its owner to review |
| `sending` | reserved by a courier; hooks cannot take it, and withdrawal waits until delivery finishes |
| `handed` | the tool has it |
| `withdrawn` | its owner took it back |

A conversation is listed with `queued`: the items that wait or are held, in the order they were said,
`[{"id", "text" (200 characters at most), "state", "at"}]`. `POST /api/conversation` with
`"action": "withdraw", "message": <id>` takes one back while it is `queued` or `paused` (answer
`{"state": "withdrawn", "message": <id>}`; refused once it was handed over). `"action": "release"` lets
what is `paused` wait again; the computer hands it over by the way that is there then (it wakes an idle
conversation for it), and answers `{"released": <count>, "way": ...}`. The computer is asked with
`session_withdraw` and `session_release`.

Where they are handed over: at the conversation's stop, joined in order, as before. Claude Code (2.1.283 or
later) is given them earlier, before its next step, as added context of its `PreToolUse` hook, so that what
is said while it works need not wait until its turn is over. Any stop (`hook` or `codex-desktop`) turns
what is `queued` for that conversation into `paused`. What still waits for an idle conversation when the
computer's program starts again is handed over by it once it has looked at the conversations (one try for
each item; after that it stays listed for its owner).

Claude courier delivery is acknowledged only by a successful `SendMessage` tool result matching its call ID. A failed delivery is never marked `handed`; an interrupted `sending` item is restored as `paused` on restart, without automatic resend. Hook wait timeouts leave the message queued until the courier reserves it.

Claude `StopFailure` ends busy/permission state, reports the API error in `last`, and pauses queued messages. It does not signal successful completion to collaboration workflows. A new user prompt clears the error. Stop attachments accept both string and array content and identify the event by `hookEvent` or `hookName`, including `Stop:*`.

Background completion reminders use an explicit completion token from a Claude stop or Codex `task_complete` record. Idle time, transcript file changes, intermediate assistant text, and a transient read result never create a completion notification. A candidate completion must remain stable across the watcher confirmation interval; if a new turn starts, it is discarded.

## Codex desktop conversation control (Windows)

`codex-desktop-control` identifies the Windows agent's native-control protocol support. A session advertises
`transport: "codex-desktop"` only after a fresh owner discovery and snapshot from the supported application,
with its working directory matching the folder discovered by the computer (whether or not it is a Remote AI
project). `reach` contains `say` while ready and while it
works (both by the channel: `thread-follower-start-turn` to one that is ready; to one at work the
application's own message for a turn in progress, `thread-follower-steer-turn` version 1, with the words and
the message as its box would restore it, `{"text", "context": {"workspaceRoots", "commentAttachments",
"collaborationMode"}}`; its answer names the running turn), and `stop` when one active turn is identified. While the
conversation shows a request of its own (`requests` in its state, or flags on its runtime status) the channel
takes no message, and `say` goes by the hooks where they tell of it (`ways.say: "hook"`). `turn` names that execution. Cached capabilities expire after 15 seconds;
every mutation opens a fresh connection and checks ownership and the discovered working folder again. Expired or incompatible
connections do not fall back to a second backend or Codex queue. What they fall back on is the tool's hooks, which
run in the application's own process: a message left for a conversation at work is handed to it when it stops.
A conversation of the desktop app that sits idle, without the application's channel, cannot be told.

The embedded reader sends `POST /api/conversation`:

```json
{"id":"0123456789abcdef0123456789abcdef","session":"11111111-2222-3333-4444-555555555555","action":"say","text":"下一句","transport":"codex-desktop"}
```

For `action: "stop"`, replace `text` with `turn` from the latest session report. The relay forwards
`session_say` / `session_stop` with the tool and project from its saved session, never a caller-supplied
filesystem path. Stop interrupts that turn, not the application process. Desktop goal tasks are excluded
from stop because this interface's expected-turn variant does not pause the goal. Hooks that already
provide a permission request continue to use `session_answer`; native approvals are not implemented.

### Remote approval scope (1.6.3)

Hook based approval requests include a stable `ask.id` and an optional `ask.options` list. The list contains `once`
(`允许一次`) and, for Claude Code only when the tool supplies a valid `permission_suggestions` entry, `always`
(`始终允许此规则`). A client sends `scope` with `action: "answer"`; omitting it means `once`. `always` is rejected
unless that request actually supplied a persistable suggestion, and it is never offered for Codex hooks. The first
answer wins; an answer for an old request is rejected after the desktop or another phone has resolved it. Claude's
`PostToolUse` and `PostToolUseFailure` hooks also clear a request resolved in the desktop application.

Only `addRules` / `allow` suggestions destined for `localSettings` are offered as persistent choices; the full rule
is shown before submission. Other persistence modes remain in the original tool. Terminal cards open the original
terminal for scope selection instead of sending an unguarded Enter key to whatever choice happens to be selected.
Codex desktop probes retire a released hook request when the application becomes idle, or transitions from its
observed question to accepting input. An active held hook is never retired by a native snapshot.

Android reminders use an opt-in `remoteMessaging` foreground service with a persistent low-priority connection
notification and serialized overview checks about ten seconds apart. Task alerts use the separate `tasks` channel.
The service keeps a baseline while the Activity is visible, suppresses task popups there, and preserves request
identities across service recreation. New questions are reported on the first background check; resolved question
notifications are cancelled. Tapping a task notification routes only to a computer already in the saved list.
Notification, battery optimization and data-saver settings are exposed in the App; there is no exact-alarm permission.
Android may still defer execution or networking during deep sleep or after a force stop.

Responses are `status: "accepted", sent: true, turn: ...` for an acknowledged native start and
`status: "confirmed", turn: ...` for an acknowledged interrupt. These are execution receipts, not completed
AI answers. A native error, malformed reply, timeout or disconnect after possible dispatch yields
`status: "unknown", sent: null`. Unknown means the action may have occurred.

The computer journals a fingerprint of action/tool/session/project/text/expected turn before dispatch.
An identical ID returns its persisted outcome across restarts; changed content is rejected. The phone's
retry uses the same payload and ID plus `receipt: true`. This is strictly a query: a missing receipt never
executes an operation, even if the relay lost the original request. Queries may run while the conversation
is busy or its native connection is unavailable. The computer still requires remote control and a verified live
conversation. Project membership is required for file operations and new tasks, but not for an already open
conversation. Native pending operations are kept in the current tab's session storage for up to one day,
including their payload for receipt queries after a reload; closing the tab or recreating the App page can
discard them. The computer's native journal contains fingerprints and results, not prompts. Hook-queued
messages are persisted separately on the computer until handed over, withdrawn or released after a stop.

Implementation: `CodexDesktopControl.cs` uses the installed package's `codex-ipc` named pipe, verifies the
server process and package path, and exchanges length-prefixed UTF-8 JSON (4-byte little-endian size,
16 MiB maximum). The inspected package is **26.1007.2314.0**; other versions fail closed. This private
desktop protocol is distinct from app-server JSON-RPC and is not a public compatibility guarantee.
Handshake and `thread-owner-discovery` version 1 identify the owner. Snapshot broadcasts are version 11;
start uses `thread-follower-start-turn` version 2 with inherited thread settings, and interrupt uses
version 4 with `expectedTurnId`. No app database, writer lock, hidden security setting or keyboard input
is modified. The adapter does not expose the snapshot's settings or full history to the relay.

Claude Windows 2.31226.1.0 contains internal session-management tools, but this adapter has no supported
external transport for them: Claude Code's conversations, its desktop app's too, are told through its hooks
and its own messages between conversations (see "The ways into a conversation"), not through a channel of
the application. Neither app has a confirmed create-and-send or close-conversation adapter here.

Validation: `tests/desktop_adapter_check.py` covers framed native transport, ownership/version checks,
permission revocation, exact-turn stop, unknown outcomes and restart replay. `relay/test_relay.py` covers
capabilities and receipt-only routing. `tests/desktop_ui_check.py` uses the real embedded page and relay
with simulated app receipts, including busy drafts, lost-response queries and stop without leaving the
reader. Those fixture checks do not substitute for real desktop send/interrupt acceptance.

## Storage

The relay keeps `terminals.json` (the list of terminals), `terminals-output/<terminal>.jsonl` (the recent output of
each, one piece per line, up to about 2,000,000 characters for a running terminal; new pieces are added to the
file rather than the file written again), `sessions.json` (hashes of sign-in tokens) and, when it made the password itself,
`password.txt`, all in its data folder with owner-only permissions where the system supports them. It writes
no terminal input or output to any log. A relay for everyone keeps the same files once per space, in
`spaces/<id>/`, with `space.json` (the hash of the space's password and when it was made) instead of a password.

## App-wide navigation and optional Windows tools

Android opens its aggregate lists even with one saved computer: four tabs over every computer (`tasks`, `projects`, `together`, `me`), the same four a computer's page draws at its foot. Its native `AppPages` reads `stats`, `chains` and `schedules` from each computer’s existing `/api/work`, with that computer’s cookie only. Failures remain local to each computer; partial totals state their coverage and sum per-computer active time. Collaboration remains scoped to one computer. No new relay operation is introduced.

Between a computer's page and the app that shows it (`window.RemoteCliNative`):

- `appTabs()` returns the tabs the app has lists for, by the page's names and separated by commas (`tasks,projects,together,me`). A press on one of those tabs in the page calls `appPage(<name>)` instead of changing the page: the app shows its own list of that name. An app without `appTabs` has lists for `together` and `me` only (1.6.1); a page in a browser has no app and keeps all four tabs to itself.
- `computer()` returns the name the phone gave this computer; the page says it in the line under its heading.
- `home()` returns to the list the app was left at. A page that the app opened for one thing calls it when that thing is left without going on to a terminal: `?new=1` (the sheet of a new conversation), `?take=<conversation>` (one conversation's window). A terminal started from there is opened with `&from=bench` and leads back the same way.
- `pref(name)` / `setPref(name, value)` keep what is the same for every computer: the look, and `newkind` (`app` or `term`), the kind of conversation the owner begins.
- The app opens a computer's page by links: `?tab=<name>` (that computer's part of a tab), `?project=<name>`, and `?tab=together&do=handoff|many|chain|schedule`, which opens that sheet of 协作 at once (`?tab=together&newchain=1` is the same as `do=chain`, from the app of 1.6.1).

Windows offers optional Claude Code and Codex CLI installation on first visible startup, and from settings. Only locally accepted, selected tools are installed. It downloads the official Windows PowerShell installers from `https://claude.ai/install.ps1` and `https://chatgpt.com/codex/install.ps1` (redirecting to `downloads.claude.ai` and `releases.openai.com`, respectively), then executes a saved script without elevation. There is no remote install API. Installed tools are detected again; native executables must pass `--version`. Logs are in the computer data folder’s `tool-install` directory. Official instructions: <https://code.claude.com/docs/en/setup> and <https://learn.chatgpt.com/docs/codex/cli>.
