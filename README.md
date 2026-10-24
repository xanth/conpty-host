# conpty-host

A `tmux.exe` for Windows, backed by the Windows pseudoconsole (ConPTY). It
implements the part of tmux that [omnigent](https://github.com/omnigent-ai/omnigent)'s
user-shell terminals call, so omnigent's tmux-driven terminal code can run on a
Windows host unchanged.

## Why

omnigent drives every terminal through tmux: it starts a private tmux server
per terminal, types into it with `send-keys`, reads the screen with
`capture-pane`, and streams the pane to the browser over tmux's control mode.
tmux doesn't exist on Windows, so omnigent refuses terminals there. Windows has
had a real pseudoconsole since Windows 10 1809. This puts tmux's interface over
it rather than teaching omnigent a second terminal backend.

## What it implements

[`docs/TMUX-CONTRACT.md`](docs/TMUX-CONTRACT.md) lists every command, flag,
format variable and control-mode message, each tied to the omnigent call site
that needs it. Native harness terminals such as Claude Code and Codex use much
more of tmux and are out of scope.

## How it works

| Piece | What it does |
| --- | --- |
| `tmux.exe` (`src/ConPtyHost.Tmux`) | Each call is a client. `new-session` starts a detached server; every later call connects to it over the `-S` socket, a Unix domain socket, which Windows 10 1803+ supports. |
| `Server/` | One session, one window, one pane. Runs the command subset, keeps options and buffers, and streams `%output` to control clients. |
| `Tmux/PaneScreen.cs` | The pane's screen, kept by [XtermSharp](https://github.com/migueldeicaza/XtermSharp), a port of xterm.js's core. `capture-pane` and the format variables read it, so a capture matches what the browser's xterm.js shows. |
| `Pty/` | The child process. On Windows, a pseudoconsole from [Microsoft.Windows.Console.ConPTY](https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY), falling back to the copy built into Windows. Elsewhere, a redirected-stdio stand-in for development and tests. |
| `Native/` | The ConPTY interop, ported from microsoft/terminal's GUIConsole.ConPTY sample. |

## Windows details that matter

These are the things that made output vanish or processes hang while this was
built. Each is handled in the code, with the reason in a comment beside it.

1. **Standard handles.** The child must be started with `STARTF_USESTDHANDLES`
   and null handles. Otherwise a host whose own stdio is redirected passes its
   pipes to the child, and the child's output skips the pseudoconsole.
2. **Duplicated pipe ends.** ConPTY duplicates the pipe ends it's given, so the
   host must close its own copies, or reads never see end-of-file.
3. **Teardown order.** Close the pseudoconsole while its output is still being
   read, then the pipes. The reverse hangs.
4. **The detached server holds nothing of the caller's.** It's started with
   handle inheritance off. omnigent waits for the client's stderr to close; a
   server holding that pipe would hang it.

## Building

```sh
nix develop      # or install a .NET 10 SDK yourself
dotnet test
```

The end-to-end test runs the built `tmux` through omnigent's command sequence.
On Windows that runs a real pseudoconsole; elsewhere, the stand-in.

## Versions

Every commit to `main` publishes `ConPtyHost.Core` to GitHub Packages once the
Windows and Linux builds pass. The version is `0.0.N`, where `N` is the
commit's git height, computed by Nerdbank.GitVersioning from `version.json`;
nothing is tagged or bumped by hand. Bump `version` in `version.json` to start
a new minor or major line.

## Licence

MIT. See `LICENSE`. The ConPTY interop is derived from a Microsoft sample, also
MIT; see `NOTICE`.
