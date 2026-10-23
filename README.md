# conpty-host

A Windows pseudoconsole (ConPTY) terminal backend, in C#, behind a transport
-agnostic interface.

## Why

Agent harnesses that drive a real terminal generally assume `tmux`: a private
tmux server plus a POSIX PTY. Neither exists on Windows, so the usual outcome
is that the terminal experience is simply unavailable there. Windows has had a
proper pseudoconsole API since Windows 10 1809 — `CreatePseudoConsole` — so the
gap is an implementation gap, not a platform one.

This repository is that implementation, kept deliberately separate from any one
host so it can be adopted by more than one.

## Shape

| Layer | What it does |
| --- | --- |
| `Native/` | 1:1 port of the ConPTY interop in [`microsoft/terminal`][sample]'s GUIConsole.ConPTY sample, modernised to source-generated `LibraryImport`. |
| `Terminal/` | `ITerminalBackend` / `ITerminalSession` — the seam that replaces "assume tmux". `ConPtyTerminalBackend` is the Windows implementation. |
| `Contracts/` | Wire types, serialised by [Nerdbank.MessagePack][nbmp] via compile-time PolyType shapes (no reflection, AOT-safe). |
| `Handlers/` | [Mediator][mediator] request handlers — source-generated dispatch, no runtime scanning. |

`ITerminalBackend` is the point of the design: a backend owns session lifetime,
bidirectional bytes, and resize, and interprets nothing. A harness TUI's escape
sequences pass through untouched.

## Lifetime rules that are easy to get wrong

Two things in the native layer are load-bearing and are the usual sources of
ConPTY bugs:

1. **Teardown order.** `ClosePseudoConsole` blocks until the attached client has
   drained, so the output pipe must still be open when it is called. Close the
   pseudoconsole *first*, then the pipes. Reversing this hangs on exit.
2. **`EXTENDED_STARTUPINFO_PRESENT`.** Without that creation flag,
   `CreateProcess` ignores the attribute list *silently* and the child inherits
   the host's console instead of the pseudoconsole. There is no error.

## Status

Scaffold. The native layer, the backend seam, the contracts and the handlers are
written; the transport host is not, because the framing has not been chosen (see
`OPEN-QUESTIONS.md`). Nothing here has been compiled or executed yet — see the
same file for why.

## Building

```sh
nix develop      # or install an 11.x SDK yourself
dotnet test
```

[sample]: https://github.com/microsoft/terminal/tree/main/samples/ConPTY/GUIConsole
[nbmp]: https://github.com/AArnott/Nerdbank.MessagePack
[mediator]: https://github.com/martinothamar/Mediator

## Licence

MIT. See `LICENSE`. The ConPTY interop is derived from a Microsoft sample,
also MIT; see `NOTICE`.
