# The tmux subset

`tmux.exe` implements exactly the tmux behaviour that omnigent's user-shell
terminals rely on, over a Windows pseudoconsole. Anything not listed here is
out of scope: native harness terminals (Claude Code, Codex) use far more of
tmux and stay unsupported on Windows.

Each entry names the omnigent call site it serves, so a change on either side
can be checked against the other. Paths are in `omnigent-ai/omnigent`.

## Invocation

```
tmux [-S socket] [-f config] [-C] command [args] [; command [args]]...
tmux -V
```

| Flag | Behaviour | Used by |
| --- | --- | --- |
| `-V` | Print `tmux 3.4`. omnigent requires 3.3 or newer and parses `major.minor`. | `util/tmux_compat.py:tmux_version` |
| `-S path` | Server socket. A Unix domain socket file, which Windows 10 1803+ supports. | every call |
| `-f path` | Accepted and ignored. omnigent passes `os.devnull`, or `/dev/null` from the bridge, to keep user config out. | `inner/terminal.py:_tmux_base_cmd`, `terminals/control_bridge.py:560` |
| `-C` | Control mode, see below. | `terminals/control_bridge.py:560` |

A `;` argument separates commands run in sequence by one client.
`inner/terminal.py:_tmux_command_sequence` relies on this for launch.

The server is a single session named `main` with one window and one pane.
`-t main` is the only target omnigent sends, so any other target is an error.

## Commands

| Command | Flags handled | Behaviour | Used by |
| --- | --- | --- | --- |
| `new-session` | `-d -s main -x W -y H -c dir shell-command` | Start the server if needed, then the pane. `shell-command` is POSIX-quoted (`shlex.join`) and is split back into an argv. | `inner/terminal.py` launch |
| `set-option` | `-g -s -q -w`, `name value` | Accepted. Only `history-limit`, `remain-on-exit` and `exit-empty` change behaviour; the rest are recorded and ignored. | `inner/terminal.py:_tmux_managed_option_commands` |
| `set-window-option` | as `set-option` | Accepted and ignored. | same |
| `set-hook` | `-g` / `-w`, `pane-died`, `detach-client -a` | `pane-died` with `detach-client -a` detaches control clients when the pane dies. Other hooks are accepted and ignored. | `inner/terminal.py` launch, `keep_alive_after_exit` |
| `send-keys` | `-t main`, `-l text`, `-H hex…`, `key…` | `-l` types text literally; `-H` sends raw bytes; otherwise each argument is a tmux key name. | `inner/terminal.py:send`, `control_bridge.py:_hex_send_keys_commands` |
| `capture-pane` | `-t main -p -e -J -S start` | Print the pane. `-e` keeps colours and attributes as SGR; `-J` joins wrapped lines; `-S -N` starts N lines into history and `-S -` at its start. | `inner/terminal.py:read`, `_exit_capture_args`, idle capture; `control_bridge.py` screen seed |
| `display-message` | `-p -t main format` | Print the expanded format. | `control_bridge.py:_capture_pane_metadata`, `_exit_capture_args` |
| `list-panes` | `-t main -F format` | Print the expanded format for the one pane. | `inner/terminal.py` exit status, `terminals/ws_common.py` |
| `has-session` | `-t main` | Exit 0 if the session exists, 1 if not. | `inner/terminal.py` liveness |
| `detach-client` | `-s main`, `-a` | Detach control clients from the session. | `inner/terminal.py` idle watchers |
| `kill-server` | | End the pane and the server. | `inner/terminal.py:close` |
| `run-shell` | `-b :` | No-op. omnigent uses it only as a round-trip probe. | `inner/terminal.py` exit-status refresh |
| `load-buffer` | `-w -b name -` | Read stdin into a named buffer. `-w` is accepted, but the buffer is not forwarded to a client clipboard. | `inner/terminal_clipboard.py` |
| `save-buffer` | `-b name -` | Write a named buffer to stdout. | `control_bridge.py:_read_tmux_buffer` |

### Key names for `send-keys`

`Enter`, `Tab`, `BSpace`, `Escape`, `Space`, `Up`, `Down`, `Left`, `Right`,
`Home`, `End`, `PageUp` / `PPage`, `PageDown` / `NPage`, `IC`, `DC`, `F1`–`F12`,
and `C-x` / `M-x` modifiers. `sys_terminal_send` documents `Enter`, `Tab`,
`C-c`, `Escape`, `C-d` and `Up`. An unknown name is typed literally, as tmux
does.

## Formats

| Variable | Value |
| --- | --- |
| `pane_dead` | `1` once the child has exited, else `0`. |
| `pane_dead_status` | The child's exit code once dead, else empty. |
| `pane_dead_signal` | Always empty: Windows processes have no signals. |
| `history_size` | Lines in scrollback above the screen. |
| `history_limit` | The `history-limit` option, default 2000. |
| `cursor_x`, `cursor_y` | Cursor column and row on screen, from 0. |
| `cursor_flag` | `1` if the cursor is visible. |
| `alternate_on` | `1` on the alternate screen. |
| `mouse_standard_flag`, `mouse_button_flag`, `mouse_all_flag` | Mouse tracking modes 1000, 1002, 1003. |
| `mouse_sgr_flag`, `mouse_utf8_flag` | Mouse encodings 1006, 1005. |
| `keypad_cursor_flag` | Application cursor keys (DECCKM). |
| `bracket_paste_flag` | Bracketed paste (2004). |
| `pane_backend` | Not in tmux. `conpty-bundled`, `conpty-inbox` or `redirected-process`, for diagnostics. |

The `omnigent` formats that combine these are
`#{pane_dead}|#{pane_dead_status}|#{pane_dead_signal}`, `#{pane_dead}`,
`#{history_size} #{history_limit}`, and the bridge's comma-joined cursor and
mode flags.

## Control mode

`tmux -S sock -C attach` keeps the client attached and speaks tmux's control
protocol on stdin and stdout.

- **Out:** `%output %0 <bytes>` for pane output, escaping every byte below
  space and the backslash as `\ooo` octal. Command replies are wrapped in
  `%begin` / `%end` (or `%error`). `%exit` ends the stream; `%window-close` is
  sent when the pane dies.
- **In:** one command per line. The bridge sends `send-keys -H -t main <hex…>`
  for input and `refresh-client -C WxH` to resize the pane.
- `%paste-buffer-changed` is never sent: there is no copy mode.

The bridge reads only `%output`, `%exit` and `%window-close`
(`control_bridge.py:_handle_control_line`) and ignores everything else.
