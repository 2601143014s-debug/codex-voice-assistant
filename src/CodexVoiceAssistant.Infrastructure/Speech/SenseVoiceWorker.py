from __future__ import annotations

import argparse
import base64
import json
import os
import re
import sys
import time

os.environ.setdefault("PYTHONUTF8", "1")

import numpy as np


REPLACEMENTS = {
    "季事本": "记事本",
    "记时本": "记事本",
    "网易云": "网易云音乐",
    "网抑云": "网易云音乐",
    "贾维思": "贾维斯",
    "贾维斯系统": "贾维斯",
}


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def normalize_text(text: str) -> str:
    normalized = text.strip()
    for source, target in REPLACEMENTS.items():
        normalized = normalized.replace(source, target)
    normalized = re.sub(r"\s+([，。！？、])", r"\1", normalized)
    return normalized.strip()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--tokens", required=True)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--language", default="auto")
    parser.add_argument("--threads", type=int, default=4)
    return parser.parse_args()


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8", newline="\n")
    args = parse_args()
    if args.runtime:
        sys.path.insert(0, args.runtime)
    import sherpa_onnx

    try:
        recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=args.model,
            tokens=args.tokens,
            num_threads=max(1, args.threads),
            use_itn=True,
            language=args.language,
            debug=False,
        )
    except Exception as error:
        emit({"type": "fatal", "error": repr(error)})
        return 1

    emit({"type": "ready"})
    for line in sys.stdin:
        request: dict = {}
        try:
            request = json.loads(line)
            command = str(request.get("command") or "transcribe")
            if command == "exit":
                emit({"ok": True})
                return 0
            if command == "warmup":
                emit({"ok": True})
                continue
            if command != "transcribe":
                raise ValueError(f"Unknown command: {command}")

            sample_rate = int(request.get("sampleRate") or 48000)
            audio_bytes = base64.b64decode(
                str(request.get("audioBase64") or "")
            )
            samples = np.frombuffer(audio_bytes, dtype="<f4").copy()
            if not len(samples):
                raise ValueError("Audio payload is empty.")

            started = time.perf_counter()
            stream = recognizer.create_stream()
            stream.accept_waveform(sample_rate, samples)
            recognizer.decode_stream(stream)
            text = normalize_text(stream.result.text or "")
            elapsed_ms = int((time.perf_counter() - started) * 1000)
            emit(
                {
                    "ok": True,
                    "text": text,
                    "elapsedMs": elapsed_ms,
                }
            )
        except Exception as error:
            emit({"ok": False, "error": repr(error)})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
