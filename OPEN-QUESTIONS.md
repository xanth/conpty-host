# Open questions

Decisions made autonomously, and what could not be resolved alone.
Ordered by how much they'd cost to change later.

## Blocking — one real defect

### 1. Output never reaches the read side

The one genuinely open bug. `A_session_echoes_what_it_is_told_to_print` is
`[Skip]`-ed so the rest of the suite can guard the repo; everything else
passes on both OSes.

**What works, verified on a `windows-latest` runner:**

- `CreatePseudoConsole` succeeds.
- `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` succeeds — the
  child starts and reports a real process id.
- `ResizePseudoConsole` succeeds against a live session.
- The first read returns data. (The earliest CI run failed on *content*, not
  on a timeout, which proves bytes flow.)

**What fails:** after that first chunk — ConPTY's terminal setup sequences —
reads block until the timeout. The program's own output never arrives and EOF
never comes. Failure is always `OperationCanceledException` from the read
timeout.

**Ruled out:**

- Not shell startup cost. Failed identically with `pwsh -NoLogo -NoProfile
  -Command "Write-Output conpty-ok"` and with `cmd.exe /c echo conpty-ok`.
- Not the timeout being too tight. 30s and 60s behave the same.
- Not our copies of the duplicated handles. Releasing `inputPipe` read side
  and `outputPipe` write side after `CreateProcess` — which is correct and
  should be kept regardless — did not change the symptom.

**Leading hypothesis, to try first:** `FileStream.ReadAsync` over an anonymous
pipe handle that was *not* created with `FILE_FLAG_OVERLAPPED`. `CreatePipe`
returns synchronous handles, and async reads over them are a known trouble
spot. The Microsoft sample reads **synchronously on a dedicated thread**, which
this port does not. Next steps, in order:

1. Replace `ReadAsync` with a blocking `Read` on a background thread, matching
   the sample.
2. Failing that, drop `FileStream` and P/Invoke `ReadFile` directly.
3. Failing that, create the pipes overlapped and use real async I/O.

This is also why the output pump (below) is unwritten: it needs this answer.

## Blocking — decisions

### 2. .NET 11 is RC until November 2026

CI builds against `11.0.100-rc.2` from the preview channel. Two things were
needed and are worth understanding before changing them:

- `allowPrerelease: true`, or `rollForward` will not consider an RC.
- A `10.0.100` floor with `latestMajor`, **not** `version: 11.0.100`. A
  prerelease sorts *below* its release in SemVer, so `11.0.100-rc.2` is lower
  than a requested `11.0.100` and could never be selected — that combination
  is unsatisfiable, and it failed with the same "no compatible SDK" message
  that a missing install gives.

Local builds still cannot work: only `10.0.301` is installed here and the TFM
is `net11.0`. Decision for tomorrow: hold the .NET 11 target, or drop to
`net10.0` so the thing builds on this machine? Nothing written so far uses a
C# 15 feature, so dropping costs nothing but the stated goal.

### 3. Transport and framing are unchosen, so there is no host project

`Contracts/` defines messages and `Handlers/` handles them, but nothing carries
them across a process boundary. Unresolved: stdio vs named pipe vs TCP
loopback; length-prefixed MessagePack vs something self-delimiting; who owns
reconnect. Not invented on purpose — it's the most expensive decision to
unpick, and "PuTTY stub" doesn't pin it down.

## Decisions worth a second opinion

### 4. Ported the Microsoft sample rather than taking a dependency

Two maintained C# ConPTY wrappers exist: **ConPtySharp** and **Porta.Pty**
(cross-platform, native shim for .NET 7+). "Write one that is 1:1" was read as
*port the canonical implementation*, so `Native/` follows microsoft/terminal's
GUIConsole.ConPTY sample in shape and call order, MIT, attributed per-file and
in `LICENSE`/`NOTICE`.

Two deliberate deviations:

- `DllImport` → source-generated `LibraryImport`, for AOT/trim safety.
- `STARTUPINFO`'s unused string fields are `nint`, not `string`, keeping
  `STARTUPINFOEX` blittable so `CreateProcess` can use `LibraryImport`. The
  sample never sets them.

If "1:1" meant byte-for-byte, both have to go — and note that deviation #1 is
entangled with the bug in §1, since the sample's synchronous read is part of
what was changed.

### 5. Stable packages over newer prereleases

| Package | Chosen | Also available |
| --- | --- | --- |
| Mediator | 3.0.2 | 3.1.0-rc.1 |
| Nerdbank.MessagePack | 1.4.95 | 1.5.22-alpha |
| TUnit | 1.76.0 | — |
| Nerdbank.GitVersioning | 3.10.94 | — |

Stable seemed right, though the project already targets an RC SDK, so the
consistency argument cuts the other way.

### 6. Windows-only tests guard at runtime

`[SupportedOSPlatform]` satisfies CA1416 but does **not** stop execution, so
the ConPTY tests also need `if (!OperatingSystem.IsWindows()) return;`. That
makes them silently *pass* on Linux rather than report as skipped. A TUnit
conditional-skip attribute would be more honest if one exists.

### 7. Repo name

`conpty-host`. Nothing depends on it; cheap to rename.

## Resolved by the first green build

Previously listed as unverified, now confirmed working: `Mediator.Unit.Value`;
`[GenerateShape]` with PolyType under central transitive pinning (no explicit
`PackageVersion` needed); `LibraryImport` against every ConPTY entry point; the
`.slnx` solution format; TUnit's `[Skip]`; and Nerdbank.GitVersioning deriving
`0.0.1.N` from git height.

Two build-config traps worth remembering: Mediator's generator **fails the
build** for any declared message without a handler — which is how
`SessionOutput`/`SessionExited` were caught — and TUnit needs
`[dotnet.test:runner]` in `dotnet.config` with a **colon**, not a dot, or the
opt-in is silently ignored and `dotnet test` still routes through VSTest. CI
sidesteps that by running the test executable directly.

## Not started

- **Output pump.** `SessionOutput` is declared and handled but nothing
  publishes it; no component drains `ITerminalSession.Output`. Blocked on §1.
- **Exit detection.** `SessionExited` likewise — no wait on the process handle.
- **The consuming side.** How a host adopts `ITerminalBackend` is unwritten.
  For omnigent that means Python over the transport from §3, replacing the
  tmux assumptions in `terminals/control_bridge.py` and
  `runner/native_controls.py`.
