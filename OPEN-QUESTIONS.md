# Open questions

Decisions I made autonomously, and the things I could not resolve alone.
Ordered by how much they'd cost to change later.

## Blocking

### 1. Nothing here has been compiled or executed

`global.json` pins SDK `11.0.100`. Only `10.0.301` is installed, so every
`dotnet` command in this repo currently fails:

```
A compatible .NET SDK was not found.
Requested SDK version: 11.0.100
```

.NET 11 / C# 15 is at RC with GA expected November 2026. **So the native layer
is unverified**: no compiler has checked the P/Invoke signatures, and no agent
has run a pseudoconsole. Treat the code as reviewed-by-eye only.

Decision needed: hold the .NET 11 pin and wait for GA, or target `net10.0` now
and bump after? Holding costs nothing but time; targeting 10 gets it compiling
today, and C# 15's union types aren't load-bearing for anything written so far.

### 2. ConPTY cannot be exercised on this machine

`CreatePseudoConsole` is Windows 10 1809+. Development is on macOS, so the
Windows-only tests in `ConPtySessionTests` are `[Skip]`-ed. CI on a
`windows-latest` runner is the only way these ever run — no workflow is written
yet. Until that exists, the Windows paths are untested by construction.

### 3. Transport and framing are unchosen, so there is no host project

`Contracts/` defines the messages, `Handlers/` handles them, but nothing
carries them across a process boundary. Unresolved: stdio vs named pipe vs TCP
loopback; length-prefixed MessagePack vs something self-delimiting; who owns
reconnect. I deliberately did not invent one — it's the decision most expensive
to unpick, and "PuTTY stub" doesn't pin it down.

## Decisions I made, worth a second opinion

### 4. Ported the Microsoft sample rather than taking a dependency

Two maintained C# ConPTY wrappers exist: **ConPtySharp** and **Porta.Pty**
(the latter cross-platform, with a native shim for .NET 7+). I read "write one
that is 1:1" as *port the canonical implementation*, so `Native/` follows
microsoft/terminal's GUIConsole.ConPTY sample in shape and call order, under
MIT with attribution in `NOTICE`.

Two deliberate deviations:
- `DllImport` → `LibraryImport` (source-generated, AOT/trim-safe).
- `STARTUPINFO`'s unused string fields are `nint`, not `string`, which keeps
  `STARTUPINFOEX` blittable so `CreateProcess` can use `LibraryImport`. The
  sample never sets them.

If "1:1" meant byte-for-byte with the sample, both deviations have to go.

### 5. Stable package versions over newer prereleases

| Package | Chosen | Also available |
| --- | --- | --- |
| Mediator | 3.0.2 | 3.1.0-rc.1 |
| Nerdbank.MessagePack | 1.4.95 | 1.5.22-alpha |
| TUnit | 1.76.0 | — |

Stable seemed right for a repo that can't yet build, but the project already
targets an RC SDK, so the consistency argument cuts the other way.

### 6. Repo name

`conpty-host`. Nothing depends on it yet; cheap to rename today.

## Unverified details — first compile will find these

- **`Unit`** — `Mediator.Unit.Value` is used as the void-request return. Confirm
  the spelling in Mediator 3.x abstractions.
- **`[GenerateShape]`** — Nerdbank.MessagePack gets shapes from PolyType. With
  `CentralPackageTransitivePinningEnabled`, PolyType may need an explicit
  `PackageVersion` entry.
- **`[Skip]`** — TUnit's skip attribute name/signature is from memory.
- **`.slnx`** — the XML solution format needs recent tooling; may need a
  classic `.sln`.

## Not started

- **Output pump.** `SessionOutput` is declared but nothing publishes it; no
  background reader drains `ITerminalSession.Output`.
- **Exit detection.** `SessionExited` likewise — no wait-on-process-handle.
- **The consuming side.** How a host actually adopts `ITerminalBackend` is
  entirely unwritten. For omnigent specifically that means Python talking to
  this over the transport from (3), and replacing the `tmux` assumptions in
  `terminals/control_bridge.py` and `runner/native_controls.py`.
