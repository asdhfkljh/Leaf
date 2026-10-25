"""Protocol/concurrency regression checks; no models or microphone required."""
import threading
import unittest
import json
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
from speech_transcription import SpeechTranscriber


class SpeechTranscriberTests(unittest.TestCase):
    def test_worker_preserves_chinese_through_ascii_stdout(self):
        # Reproduce a GUI-launched worker with a restrictive inherited pipe encoding.
        code = (
            "from realtime_worker import emit; "
            "text = '\\u4f60\\u597d\\uff0c\\u4e16\\u754c'; "
            "emit({'type': 'transcript', 'text': text}); "
            "emit({'type': 'features', 'transcript': text})"
        )
        result = subprocess.run(
            [sys.executable, "-c", code], cwd=str(Path(__file__).resolve().parent),
            env=dict(os.environ, PYTHONIOENCODING="ascii:replace"),
            capture_output=True, check=True,
        )
        preview, final = [json.loads(line) for line in result.stdout.decode("ascii").splitlines()]
        self.assertEqual(preview["text"], "你好，世界")
        self.assertEqual(final["transcript"], "你好，世界")

    def test_busy_preview_is_not_queued_and_reset_discards_old_result(self):
        entered, release = threading.Event(), threading.Event()
        calls, packets = [], []

        class Model:
            def transcribe(self, audio, **options):
                calls.append((audio, options))
                entered.set()
                if not release.wait(3):
                    raise RuntimeError("test decode timed out")
                return [SimpleNamespace(text="old")], None

        transcriber = SpeechTranscriber(Model(), "auto", packets.append)
        try:
            transcriber.preview("old audio")
            self.assertTrue(entered.wait(3))
            transcriber.preview("must not queue")
            transcriber.reset()
            release.set()
            transcriber.pending.result(timeout=3)
            self.assertEqual(len(calls), 1)
            self.assertIsNone(calls[0][1]["language"])
            self.assertEqual(packets, [])
        finally:
            release.set()
            transcriber.close()

    def test_final_joins_segments_and_uses_requested_language(self):
        options_seen = []

        class Model:
            def transcribe(self, audio, **options):
                options_seen.append(options)
                return [SimpleNamespace(text=" 你好"), SimpleNamespace(text="世界 ")], None

        transcriber = SpeechTranscriber(Model(), "zh", lambda packet: None)
        try:
            self.assertEqual(transcriber.finalize("audio"), "你好世界")
            self.assertEqual(options_seen[0]["language"], "zh")
            self.assertTrue(options_seen[0]["vad_filter"])
        finally:
            transcriber.close()


if __name__ == "__main__":
    unittest.main()
