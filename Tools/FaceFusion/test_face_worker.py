import base64
import json
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

import cv2
import numpy as np
from face_worker import extract


class ExtractionTests(unittest.TestCase):
    def setUp(self):
        self.frame = np.random.default_rng(7).integers(0, 255, (480, 640, 3), dtype=np.uint8)
        self.points = [SimpleNamespace(x=.5, y=.5) for _ in range(478)]
        for ids, cx, cy in [([33, 133, 159, 145, 70, 63, 105, 66, 107], .35, .38),
                            ([263, 362, 386, 374, 300, 293, 334, 296, 336], .65, .38),
                            ([61, 291, 0, 17, 13, 14], .5, .65)]:
            for i, index in enumerate(ids):
                self.points[index] = SimpleNamespace(x=cx + (i % 3 - 1) * .025, y=cy + (i % 2 - .5) * .03)
        self.points[33] = SimpleNamespace(x=.32, y=.38)
        self.points[263] = SimpleNamespace(x=.68, y=.38)

    def test_three_regions_return_decodable_atlas(self):
        atlas = extract(self.frame, self.points, cv2, np)
        image = cv2.imdecode(np.frombuffer(base64.b64decode(atlas), dtype=np.uint8), cv2.IMREAD_COLOR)
        self.assertEqual(image.shape, (128, 768, 3))
        self.assertGreater(float(image.var()), 10)

    def test_large_yaw_rejected(self):
        self.points[1].x = .9
        self.assertIsNone(extract(self.frame, self.points, cv2, np))

    def test_camera_roll_rejected(self):
        self.points[263].y = .8
        self.assertIsNone(extract(self.frame, self.points, cv2, np))

    def test_blurred_face_rejected(self):
        self.assertIsNone(extract(np.zeros_like(self.frame), self.points, cv2, np))

    def test_clipped_crop_rejected(self):
        self.points[70].x = -.1
        self.assertIsNone(extract(self.frame, self.points, cv2, np))

    def test_small_face_rejected(self):
        self.points[33].x = .49
        self.points[263].x = .51
        self.assertIsNone(extract(self.frame, self.points, cv2, np))

    def test_atlas_slots_preserve_image_left_right(self):
        self.frame[:, :300] = (0, 0, 255)
        self.frame[:, 340:] = (255, 0, 0)
        atlas = extract(self.frame, self.points, cv2, np)
        image = cv2.imdecode(np.frombuffer(base64.b64decode(atlas), dtype=np.uint8), cv2.IMREAD_COLOR)
        self.assertGreater(image[:, :256, 2].mean(), 200)
        self.assertGreater(image[:, 256:512, 0].mean(), 200)


class WorkerTests(unittest.TestCase):
    def test_model_no_face_and_malformed_frame_recovery(self):
        model = Path(__file__).with_name('face_landmarker.task')
        self.assertTrue(model.exists(), 'Download official model before running integration tests')
        ok, jpeg = cv2.imencode('.jpg', np.zeros((480, 640, 3), dtype=np.uint8))
        self.assertTrue(ok)
        payload = json.dumps({'id': 9, 'image': base64.b64encode(jpeg).decode(), 'rotation': 90})
        worker = subprocess.run([sys.executable, str(Path(__file__).with_name('face_worker.py'))],
                                input='{"id":8,"image":"bad"}\n' + payload + '\n',
                                text=True, capture_output=True, timeout=60)
        self.assertEqual(worker.returncode, 0, worker.stderr)
        replies = [json.loads(line) for line in worker.stdout.splitlines()]
        self.assertEqual(replies[0]['type'], 'ready')
        self.assertEqual(replies[1]['type'], 'error')
        self.assertEqual(replies[2], {'type': 'face', 'id': 9, 'atlas': ''})


if __name__ == '__main__':
    unittest.main()
