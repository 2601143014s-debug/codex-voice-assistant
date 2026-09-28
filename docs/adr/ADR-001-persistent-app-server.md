# ADR-001: Persistent Codex app-server

## Status

Accepted.

## Decision

The voice assistant owns one long-lived `codex app-server --listen stdio://`
process. Each recognized transcript starts a turn in the same persistent Codex
thread.

## Reasons

- `codex exec` process startup adds roughly ten seconds or more per utterance.
- One app-server session avoids repeated plugin, skill, provider, and
  authentication initialization.
- The app-server protocol streams `item/agentMessage/delta` and supports
  `turn/interrupt`.
- DeepSeek remains the configured Codex model provider.

## Verified Protocol

```text
initialize
initialized
thread/start
turn/start
item/agentMessage/delta
turn/completed
```

The first live protocol probe completed in approximately 1.3 seconds with:

```text
thread=01a0be3f-7623-73b3-a419-1dcb5edb5fe8
text=PROTOCOL_OK
```

## Required Invariants

- Exactly one writer owns the app-server stdin stream.
- Responses and server requests are dispatched separately.
- Every turn event is filtered by both thread ID and turn ID.
- Delayed events from an interrupted turn never enter the next turn.
- Startup, first delta, inactivity, and turn duration have separate timeouts.
- A dead app-server is killed with its process tree and rebuilt before retry.
