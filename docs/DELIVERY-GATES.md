# Delivery Gates

The old `JarvisNative` application is no longer the active delivery target.
The rewrite is installed only after the checks below pass.

## Core

- [x] One persistent Codex app-server process.
- [x] DeepSeek is the only configured model provider.
- [x] Recognized text is passed unchanged to the Codex voice thread.
- [x] No DeepSeek SDK or local intent model is used by the voice path.
- [x] Every event is filtered by connection, thread ID, and turn ID.
- [x] Interrupted turns cannot contaminate later turns.
- [x] A dead app-server is rebuilt before the next turn.

## Audio

- [x] Continuous WASAPI capture starts automatically.
- [x] Speaker loopback frames carry monotonic timestamps and are matched to
  microphone frames with an endpoint-specific tolerance.
- [x] WebRTC AEC and noise suppression are always active.
- [x] Silero neural VAD rejects high-volume non-speech noise before Whisper.
- [x] Microphone capture remains active during TTS playback.
- [x] Barge-in stops synthesis and playback before starting the next turn.
- [x] Device loss is fault-injected in self-test; capture reopens and resumes
  without restarting Windows.
- [x] Whisper runs in an isolated process and is restarted after a native
  Vulkan crash.
- [x] Kokoro runs in a persistent isolated process and is warmed at startup.

## Recognition and Speech

- [x] SenseVoiceSmall int8 is the primary warmed recognizer; Whisper remains
  available as a fallback.
- [x] Silence does not stop the listener.
- [x] TTS begins after the first complete sentence.
- [x] Late audio from an interrupted turn is discarded.
- [x] Vulkan is selected before CPU inference when available.
- [x] SenseVoice never simulates Win+H and has no popup-window dependency.
- [x] The primary TTS is the local full-precision Kokoro Chinese 82M model
  with a male-only `zm_*` voice and OneCore male fallback.

## UI and Lifecycle

- [x] The visible UI is one full-width sci-fi bar docked above the taskbar and
  one tray icon.
- [x] The bar glow is driven by measured TTS PCM amplitude, not a timer or
  decorative animation.
- [x] Kokoro `zm_098` is primary, with OneCore male fallback.
- [x] No settings window appears unless explicitly requested.
- [x] Stop and exit drain capture, ASR, Codex, TTS, and child processes.
- [x] A second instance signals the first instance and exits.
- [x] All WPF updates are marshalled to the UI dispatcher.
- [x] A persistent Startup-folder watcher starts the assistant when Codex
  appears and stops it when Codex exits.

## Verification

- [x] Unit tests cover state transitions, interruption, failure isolation,
  sentence chunking, VAD, speech cancellation, and JSON-RPC behavior.
- [x] Integration tests cover app-server restart, stale readers, pending
  request failure, first-delta timeout disarming, and late-event isolation.
- [x] Self-test covers hardware capture, device recovery, microphone capture
  during playback, Whisper recognition, Codex execution, and TTS.
- [x] The active input and output endpoint names are included in self-test
  evidence.
- [x] A live protocol turn created a file on disk through Codex (`TOOL_OK`),
  proving the voice backend can operate the computer.
- [x] Clean install, idempotent repair, rollback infrastructure, and uninstall
  passed in an isolated clean profile. Windows Sandbox itself requires
  elevation and was not used.
- [x] Logs include timings but never raw audio or API keys, and rotate at
  10 MB.
- [x] Release artifacts are versioned, SHA-256 verified, and recorded in an
  installation manifest.

## Residual Hardware Matrix

- The tested host used its default communication microphone/render endpoints.
- Bluetooth and HDMI latency paths are handled by endpoint-specific delay
  settings, but those physical endpoints were not available for removal and
  reconnect testing on this machine.
- Windows UAC is not bypassed. User-level applications and files are directly
  controllable; an elevation prompt is still required for administrator-only
  operations.
