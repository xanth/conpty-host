# Open questions

What still needs a decision or a check, ordered by how much it would cost to
get wrong.

## 1. How omnigent gets `tmux.exe`

omnigent finds tmux with `shutil.which("tmux")`, so `tmux.exe` has to be on the
host's `PATH`. Nothing installs it yet. Options:

- **A .NET global tool** (`dotnet tool install -g`), published to the existing
  GitHub Packages feed. Simple, but it needs a .NET runtime on the host and a
  feed credential.
- **A self-contained single-file `tmux.exe`** attached to a GitHub release,
  which omnigent's Windows setup downloads. No runtime needed, but `conpty.dll`
  and `OpenConsole.exe` must travel with it.

Either way, `PATH` puts it ahead of any other `tmux.exe`, which matters on a
machine with Git Bash or MSYS2's tmux installed.

## 2. Whether the bundled ConPTY is actually used

`conpty.dll` and `OpenConsole.exe` come from Microsoft's package, with a fallback
to the copy built into Windows. Where the package's build targets put
`OpenConsole.exe` relative to `conpty.dll` differs between a plain build and a
RID-specific publish. `#{pane_backend}` reports `conpty-bundled` or
`conpty-inbox`, and the end-to-end test logs it on Windows CI, so check that log
before relying on the newer console host.

## 3. Job objects

The server starts with `CREATE_BREAKAWAY_FROM_JOB`, falling back to staying in
the caller's job when the job forbids breakaway. If omnigent's Windows runner
places terminals in a kill-on-close job that forbids breakaway, the server
dies with its caller and the shell with it. Unverified until omnigent runs it.

## 4. Fidelity limits, by design for now

- **Colour:** XtermSharp keeps a 256-colour palette, so 24-bit colour is
  matched to the nearest palette entry in `capture-pane -e`. The browser
  receives raw `%output` bytes, so what the user sees is unaffected.
- **Copy mode:** none, so `%paste-buffer-changed` is never sent.
- **`load-buffer -w`:** stores the buffer but doesn't forward it to a client
  clipboard.
- **Off Windows** the stand-in is a redirected process, not a terminal, so only
  line-oriented programs work there. It exists to test the tmux layer.

## 5. XtermSharp is unmaintained

The last commit was in November 2022, and the NuGet package is
`1.0.0-alpha.10`. It's MIT and small enough to vendor if a fix is ever needed.

## Resolved

- **Output never reached the read side.** The child inherited the host's
  redirected standard handles. Fixed with `STARTF_USESTDHANDLES` and null
  handles, verified on Windows CI.
- **Transport.** Newline-delimited JSON frames over the `-S` Unix domain
  socket, between our own client and server only. omnigent never sees them; it
  sees tmux's interface.
- **Target: .NET 10 / C# 14**, decided 2026-10-24.
