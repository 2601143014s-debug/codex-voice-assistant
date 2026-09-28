# Codex Voice Assistant

Windows delivery rewrite that turns Codex into a continuous voice frontend.
Recognition is sent to a persistent Codex app-server thread as ordinary text;
Codex and the configured DeepSeek provider execute the requested computer
operations.

## Runtime

```text
WASAPI microphone + speaker loopback
  -> WebRTC AEC / noise suppression / gain control
  -> Silero neural VAD
  -> persistent SenseVoiceSmall int8 worker
  -> persistent Codex app-server
  -> DeepSeek
  -> streaming sentence TTS (local Kokoro 82M Chinese neural voice)
```

The visible UI is a full-width sci-fi voice bar docked directly above the
Windows taskbar, plus a tray icon. Speech starts a new turn immediately and
cancels the active Codex turn and TTS playback.
The bar glow follows the actual PCM output level of the spoken reply.
Kokoro runs in a persistent isolated Python worker, so the 82M model and
Chinese G2P are loaded once at startup. The default male voice is `zm_098`,
selected from all 45 male voices for the lowest measured fundamental
frequency, and speech remains fully local and offline after installation.
SenseVoice runs in a persistent, isolated CPU process. It is the Chinese-first
primary recognizer; Whisper large-v3-turbo remains installed as a fallback.
The application never simulates Win+H or depends on its popup window.

## Requirements

- Windows 10 or 11 x64.
- Codex Desktop/CLI with the existing DeepSeek provider configured in
  `%USERPROFILE%\.codex\config.toml`.
- `DEEPSEEK_API_KEY` available to Codex.
- A 48 kHz default communication microphone and render endpoint.
- Windows multimedia render endpoint is used for TTS and echo reference.
- No system-wide .NET installation is required; the app is self-contained.

## Install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Launch
```

The installer:

- publishes a self-contained release;
- verifies Whisper, Silero VAD, and Kokoro models against pinned SHA-256
  hashes;
- verifies the SenseVoiceSmall int8 model and `sherpa-onnx` runtime;
- installs the pinned portable Python/Kokoro runtime and Chinese voice pack;
- installs under `%LOCALAPPDATA%\Programs\CodexVoiceAssistant`;
- switches the `current` junction atomically;
- preserves a rollback path for upgrades;
- registers a hidden sign-in watcher that starts the assistant only after
  the Codex desktop app appears.

Repair the active release:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Repair -Launch
```

## Uninstall

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Add `-RemoveModels` to remove Whisper/Kokoro models and the Kokoro runtime.
Add `-RemoveSettings` to remove settings and logs.

## Diagnostics

Run the complete installed-path self-test:

```powershell
& "$env:LOCALAPPDATA\Programs\CodexVoiceAssistant\current\CodexVoiceAssistant.exe" --self-test
Get-Content "$env:TEMP\CodexVoiceAssistant-self-test.json"
```

The report covers hardware capture, an injected device restart, capture during
TTS, Whisper recognition, a live Codex turn, and TTS.

Runtime logs are stored at:

```text
%LOCALAPPDATA%\CodexVoiceAssistant\voice-assistant.log
```

Settings are stored at:

```text
%LOCALAPPDATA%\CodexVoiceAssistant\settings.json
```

## Build and Test

```powershell
dotnet build CodexVoiceAssistant.sln -c Release -warnaserror
dotnet test CodexVoiceAssistant.sln -c Release
dotnet run --project tools\CodexVoiceAssistant.ProtocolProbe -c Release
dotnet run --project tools\CodexVoiceAssistant.AudioProbe -c Release
```

Delivery status and remaining hardware-matrix notes are tracked in
`docs\DELIVERY-GATES.md`.

## Permissions and Voice

The assistant uses Codex's `danger-full-access` and `approvalPolicy=never`
settings so it can operate the current user's files and applications without a
second confirmation loop. Windows UAC is not bypassed: administrator-only
operations still require the operating system's elevation prompt.

The default TTS provider is the local Kokoro 82M Chinese neural model with
male voice `zm_098` and `KokoroSpeed` set to `1.08`. It is a fast, original
science-fiction-style male voice selected objectively from the 45 `zm_*`
voices in the model bundle. It is not a Paul Bettany/JARVIS voice clone.
OneCore `Microsoft Kangkang` remains the male-only fallback if the local
Kokoro runtime is unavailable. Edge TTS and legacy SAPI are retained as
optional providers. Change `TtsProvider`, `Voice`, and `KokoroSpeed` in
`settings.json`; the voice selector rejects every non-`zm_*` Kokoro voice.

## Speech Recognition

The default recognizer is `SenseVoiceSmall int8`, optimized for Chinese and
mixed Chinese/English commands. It is loaded once into a persistent worker and
does not require any key press or Win+H simulation. The measured self-test
latency is typically about 160-300 ms, with Whisper large-v3-turbo retained as
an automatic fallback if the SenseVoice runtime is unavailable.
