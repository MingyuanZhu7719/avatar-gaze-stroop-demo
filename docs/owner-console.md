# Development owner console

This is a separate desktop owner console for **simulation development only**. It adds no participant-facing mode selector, account system, network service, or production access control. Apparatus sessions remain globally blocked. A person or process that can write the explicitly selected local file has owner-command authority; keep that file in an owner-controlled directory. Do not expose it through a shared or synchronized folder.

The participant host opts in at launch with:

```text
--stroop-owner-commands /absolute/path/one-session.commands
```

Use a fresh path for each new simulation session. The dispatcher binds the path to the host's explicit session ID. It refuses a different session and holds an exclusive dispatcher lock for its lifetime. Launch and adapter wiring belong to the participant host; enabling a file path does not authorize apparatus operation or bypass readiness checks.

Run the separately compiled executable through `python3 scripts/owner.py` (installed Unity Mono or existing .NET SDK), or `dotnet run --project AvatarStroop.OwnerConsole.csproj --`. For example:

```sh
python3 scripts/owner.py /absolute/path/one-session.commands practice
```

The executable accepts these commands:

```text
OwnerConsole /absolute/path/one-session.commands practice
OwnerConsole /absolute/path/one-session.commands lock automatic
OwnerConsole /absolute/path/one-session.commands lock simulator
OwnerConsole /absolute/path/one-session.commands lock live
OwnerConsole /absolute/path/one-session.commands begin
OwnerConsole /absolute/path/one-session.commands export
```

Run one `lock` choice in the appropriate block-preparation phase. `practice` invokes owner practice entry; `lock` invokes the existing owner source lock; `begin` invokes the existing countdown/readiness guard. `live` remains an explicit CLI choice, but the development command binding rejects it before changing the block lock because real tracking is unavailable. The application readiness guard also remains closed for it. It never falls back to the simulator or automatic source. A locked source cannot be changed at runtime. There are no commands to override condition, order, B initial target, trial lists, pause recovery, or remote resume. `export` invokes only a fixed host-supplied callback; command records cannot supply an export path, script, or additional arguments.

The console prints a unique request ID, appends the command, and waits up to two seconds for a receipt. `QUEUED` means execution is unconfirmed. A retry can reuse the exact request ID and exact command:

```text
OwnerConsole /absolute/path/one-session.commands begin --id 0123456789abcdef0123456789abcdef
```

An acknowledged ID never executes again, including after dispatcher restart. Reusing an ID for different content is rejected. Rejected commands require a new ID only after the owner has resolved the cause and intends a new attempt. For an uncertain outcome, inspect application state first; this IPC does not offer a recovery action.

The file protocol is strict ASCII with LF-terminated records:

```text
ASOWNER|1|<canonical lowercase GUID-N>|Practice|~
ASOWNER|1|<canonical lowercase GUID-N>|Lock|SimulatorRehearsal
```

The only verbs are `Practice`, `Lock`, `Begin`, and `Export`. Only `Lock` accepts a required source: `Automatic`, `SimulatorRehearsal`, or `LiveWoz`. Incomplete final records are retained until a newline arrives. Writers refuse to append onto an incomplete tail. Invalid records receive rejection receipts and cannot invoke the handler. Previously consumed content cannot be changed or truncated; the dispatcher fails closed if this occurs. Development files have a 1 MiB limit and commands have a 256-byte limit.

Sibling files are implementation audit state: `.session` binds identity, `.owner-lock` prevents concurrent dispatchers, `.pending` records durable intent, and `.ack` contains accepted/rejected receipts with request ID, command hash, and a base64-encoded UTF-8 reason. The escaping preserves delimiters, newlines, and Unicode without creating extra records. Do not edit or remove these files while a queue is in use. Damaged or incomplete journals require review. An intent without a final receipt after restart is rejected as an uncertain previous outcome and is never replayed automatically. Handler exceptions also report possible uncertainty. This avoids claiming atomic exactly-once execution across application side effects and file persistence.

The host creates `OwnerCommandDispatcher(path, sessionId, handler)`, calls `DispatchPending()` on its main loop, reports I/O errors visibly, and disposes it at shutdown. The handler returns `null` for acceptance or a reason for a rejected action. `OwnerCommandExecution.Execute` binds the typed command to supplied practice, lock, begin, and fixed export callbacks. These helpers have no socket or Unity dependency.

Regression coverage checks parsing, escaping, incomplete input, receipts, duplicate IDs, identity conflicts, wrong-phase source locking, fixed allocation factors, blocked live readiness, absent source fallback, uncertain outcomes, exclusive dispatch, session mismatch, damaged journals, and truncation. Unity Editor execution and standalone rendered UI remain separate verification steps; this console does not establish either.
