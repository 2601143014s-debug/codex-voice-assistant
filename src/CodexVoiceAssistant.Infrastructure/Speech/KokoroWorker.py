from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
import time
from pathlib import Path

os.environ.setdefault("ORT_LOG_SEVERITY_LEVEL", "3")
os.environ.setdefault("PYTHONUTF8", "1")

from misaki.zh import ZHG2P
from kokoro_onnx import Kokoro
import numpy as np
import soundfile as sf


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def clean_text(text: str) -> str:
    text = re.sub(r"```.*?```", " ", text, flags=re.DOTALL)
    text = re.sub(r"`([^`]*)`", r"\1", text)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
    text = re.sub(r"https?://\S+", " ", text)
    text = re.sub(r"[*_#|~]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def normalize_audio(samples: np.ndarray) -> np.ndarray:
    audio = np.asarray(samples, dtype=np.float32)
    if not len(audio):
        return audio

    # Remove electrical rumble while retaining the low male timbre.
    sample_rate = 24000
    rc = 1.0 / (2.0 * np.pi * 65.0)
    dt = 1.0 / sample_rate
    alpha = rc / (rc + dt)
    filtered = np.empty_like(audio)
    previous_input = 0.0
    previous_output = 0.0
    for index, value in enumerate(audio):
        current = float(value)
        output = alpha * (previous_output + current - previous_input)
        filtered[index] = output
        previous_input = current
        previous_output = output

    audio = np.tanh(filtered * 1.18) / np.tanh(1.18)
    peak = float(np.max(np.abs(audio)))
    if peak > 0:
        audio = audio * min(0.93 / peak, 1.0)
    return audio.astype(np.float32)


class KokoroWorker:
    def __init__(self, model_path: str, voices_path: str) -> None:
        self.kokoro = Kokoro(model_path, voices_path)
        self.g2p = ZHG2P()

    def phonemize(self, text: str) -> str:
        segments: list[str] = []
        for segment in re.findall(
            r"[\u4E00-\u9FFF]+|[^\u4E00-\u9FFF]+",
            text,
        ):
            if re.search(r"[\u4E00-\u9FFF]", segment):
                segments.append(ZHG2P.legacy_call(segment))
                continue
            english = self.kokoro.tokenizer.phonemize(
                segment,
                "en-us",
            )
            if english:
                segments.append(english)
        return " ".join(segment for segment in segments if segment).strip()

    def synthesize(
        self,
        text: str,
        voice: str,
        speed: float,
        output_path: str,
    ) -> tuple[float, int]:
        cleaned = clean_text(text)
        if not cleaned:
            raise ValueError("No speakable text remains after cleaning.")

        phonemes = self.phonemize(cleaned)
        if not phonemes:
            raise ValueError("Chinese G2P produced no phonemes.")

        started = time.perf_counter()
        samples, sample_rate = self.kokoro.create(
            phonemes,
            voice=voice,
            speed=speed,
            is_phonemes=True,
            trim=True,
            sentence_pause=0.18,
            clause_pause=0.08,
        )
        samples = normalize_audio(samples)
        target = Path(output_path).resolve()
        target.parent.mkdir(parents=True, exist_ok=True)
        sf.write(target, samples, sample_rate, subtype="PCM_16")
        return len(samples) / sample_rate, int(
            (time.perf_counter() - started) * 1000
        )

    def warmup(self, voice: str, speed: float) -> int:
        started = time.perf_counter()
        with tempfile.TemporaryDirectory() as directory:
            self.synthesize(
                "\u7cfb\u7edf\u5df2\u5c31\u7eea\u3002",
                voice,
                speed,
                str(Path(directory) / "warmup.wav"),
            )
        return int((time.perf_counter() - started) * 1000)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--voices", required=True)
    parser.add_argument("--default-voice", default="zm_098")
    parser.add_argument("--default-speed", type=float, default=1.08)
    return parser.parse_args()


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8", newline="\n")
    args = parse_args()

    try:
        worker = KokoroWorker(args.model, args.voices)
    except Exception as error:
        emit({"type": "fatal", "error": repr(error)})
        return 1

    emit({"type": "ready"})
    for line in sys.stdin:
        request: dict = {}
        try:
            request = json.loads(line)
            request_id = int(request.get("id", 0))
            request_type = request.get("type", "synthesize")
            voice = str(request.get("voice") or args.default_voice)
            speed = float(request.get("speed") or args.default_speed)
            speed = min(2.0, max(0.5, speed))

            if request_type == "shutdown":
                emit({"type": "result", "id": request_id, "ok": True})
                return 0

            if request_type == "warmup":
                elapsed_ms = worker.warmup(voice, speed)
                emit(
                    {
                        "type": "result",
                        "id": request_id,
                        "ok": True,
                        "elapsedMs": elapsed_ms,
                    }
                )
                continue

            if request_type != "synthesize":
                raise ValueError(f"Unknown request type: {request_type}")

            output_path = str(request.get("output") or "")
            if not output_path:
                raise ValueError("Missing output path.")
            duration, elapsed_ms = worker.synthesize(
                str(request.get("text") or ""),
                voice,
                speed,
                output_path,
            )
            emit(
                {
                    "type": "result",
                    "id": request_id,
                    "ok": True,
                    "output": output_path,
                    "durationSeconds": duration,
                    "elapsedMs": elapsed_ms,
                }
            )
        except Exception as error:
            emit(
                {
                    "type": "result",
                    "id": int(request.get("id", 0))
                    if isinstance(request, dict)
                    else 0,
                    "ok": False,
                    "error": repr(error),
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
