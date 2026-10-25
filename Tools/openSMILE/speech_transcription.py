"""Single background decoder, with bounded pending work and stale-preview rejection."""
from concurrent.futures import ThreadPoolExecutor
import threading


class SpeechTranscriber:
    def __init__(self, model, language, emit):
        self.model = model
        self.language = None if language == "auto" else language
        self.emit = emit
        self.executor = ThreadPoolExecutor(max_workers=1)
        self.pending = None
        self.generation = 0
        self.lock = threading.Lock()

    def reset(self):
        with self.lock:
            self.generation += 1

    def decode(self, audio):
        segments, _ = self.model.transcribe(
            audio, language=self.language, beam_size=1,
            vad_filter=True, condition_on_previous_text=False,
        )
        return "".join(segment.text for segment in segments).strip()

    def preview(self, audio):
        if self.pending is not None and not self.pending.done():
            return  # Never queue a growing backlog of stale snapshots.
        generation = self.generation

        def work():
            try:
                text = self.decode(audio)
                with self.lock:
                    if generation == self.generation:
                        self.emit({"type": "transcript", "text": text})
            except Exception as exception:
                with self.lock:
                    if generation == self.generation:
                        self.emit({"type": "transcription_warning", "message": str(exception)})

        self.pending = self.executor.submit(work)

    def finalize(self, audio):
        self.reset()  # A pending preview must never overwrite the final transcript.
        if self.pending is not None:
            self.pending.result()
        return self.decode(audio)

    def close(self):
        self.reset()
        self.executor.shutdown(wait=True)
