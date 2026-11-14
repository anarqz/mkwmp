# In-game overlay & notifications

> **Superseded for pre-game states (2026-11-13).** The queue/seat UX now lives
> in the Rust **client** (`rust/client`): it shows "Nth in queue", and when the
> seat is granted it writes `mkwmp.toml` and launches the game. See
> docs/ARCHITECTURE.md. An in-game overlay is only needed for *mid-race*
> messages (disconnects, etc.), which are rare and currently log-only. The
> notification core below still stands if we want them on screen later.


The server tells the DLL what to show the player ("Connected to MKWMP",
"Waiting to join, 4th in queue"). Three layers, decoupled so each can land
independently:

```
server --(transport)--> notify_push() --(queue)--> renderer
                         [notify.c, done]          [overlay window, TODO]
                                 |
                                 +--> mkwmp.log (always)
```

## 1. Core (done) — `native/probe/src/notify.c`

`notify_push(level, text)` enqueues a notification (INFO/SUCCESS/WARN/ERROR),
thread-safe, logs it, drops oldest on overflow. `notify_drain(out, max)` pulls
pending ones for a renderer. Unit-tested. Everything below builds on this.

## 2. Transport (TODO) — server → DLL

The DLL needs a channel the server can push to mid-session. Options:
- **Dedicated side stream (preferred):** the DLL opens one extra connection to
  the mkwmp server (not NPLN) carrying length-prefixed `{level, text}` frames.
  Simple, decoupled from the game's RPCs, server pushes any time.
- Piggyback on an open NPLN server-stream (e.g. the matchmaking Track stream):
  no new socket, but limited to windows when such a stream is open, and the
  payload must stay inside a shape the game ignores. Fragile.

Recommendation: the side stream. A tiny reader thread in the DLL calls
`notify_push` per frame. Queue position etc. are just text the server formats.

## 3. Renderer (TODO) — showing it

This is a Vulkan game (SDL3). Two options:
- **Win32 top-most layered window (preferred first cut):** the DLL creates a
  borderless, click-through, always-on-top window over the game and draws
  toasts (GDI/Direct2D). No graphics-API hooking. Works in borderless-windowed
  (the game's mode). ~200 lines. Fights z-order only in exclusive fullscreen.
- **`vkQueuePresentKHR` hook:** composites into the game's own frames — the
  "proper" overlay, but a font atlas + Vulkan pipeline + present hook, a few
  hundred fragile lines. Revisit only if the Win32 window proves insufficient.

The renderer calls `notify_drain` on a timer, shows each toast for a few
seconds, fades out. Until it exists, notifications are still captured in
`mkwmp.log`, so nothing is lost.

## Status

Core done and tested. Transport and renderer are post-race-milestone: they
improve UX but do not block two clients racing. Build them once the match
path works end to end.
