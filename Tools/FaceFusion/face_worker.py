"""JSON-lines image processor. Camera ownership and frame timing stay in Unity."""
import base64
import json
import sys
from pathlib import Path


def extract(frame, landmarks, cv2, np):
    h, w = frame.shape[:2]
    p = np.array([(v.x * w, v.y * h) for v in landmarks], dtype=np.float32)
    # Reject clipped faces, strong yaw/roll, and unusably small or blurred captures.
    eye_a, eye_b = p[33], p[263]
    span = float(np.linalg.norm(eye_b - eye_a))
    if span < 65 or abs(float(eye_b[1] - eye_a[1])) > span * .22:
        return None
    nose = p[1]
    mid = (eye_a + eye_b) / 2
    if abs(float(nose[0] - mid[0])) > span * .23:
        return None
    regions = [[33, 133, 159, 145, 70, 63, 105, 66, 107],
               [263, 362, 386, 374, 300, 293, 334, 296, 336],
               [61, 291, 0, 17, 13, 14]]
    # Keep image-left eye in slot zero regardless of landmark naming.
    regions[:2] = sorted(regions[:2], key=lambda ids: float(p[ids, 0].mean()))
    atlas = np.zeros((128, 768, 3), dtype=np.uint8)
    for i, ids in enumerate(regions):
        points = p[ids]
        lo, hi = points.min(axis=0), points.max(axis=0)
        pad = (hi - lo) * np.array([.18, .30])
        x0, y0 = np.floor(lo - pad).astype(int)
        x1, y1 = np.ceil(hi + pad).astype(int)
        if x0 < 0 or y0 < 0 or x1 >= w or y1 >= h or x1 <= x0 or y1 <= y0:
            return None
        crop = frame[y0:y1, x0:x1]
        if i == 2 and cv2.Laplacian(cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY), cv2.CV_64F).var() < 12:
            return None
        atlas[:, i * 256:(i + 1) * 256] = cv2.resize(crop, (256, 128))
    ok, png = cv2.imencode('.png', atlas)
    return base64.b64encode(png).decode('ascii') if ok else None


def main():
    import cv2
    import mediapipe as mp
    import numpy as np
    model = Path(__file__).with_name('face_landmarker.task')
    options = mp.tasks.vision.FaceLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(model)), num_faces=1,
        min_face_detection_confidence=.7, min_face_presence_confidence=.7)
    with mp.tasks.vision.FaceLandmarker.create_from_options(options) as detector:
        print(json.dumps({'type': 'ready'}), flush=True)
        for line in sys.stdin:
            try:
                packet = json.loads(line)
                raw = np.frombuffer(base64.b64decode(packet['image']), dtype=np.uint8)
                frame = cv2.imdecode(raw, cv2.IMREAD_COLOR)
                if frame is None:
                    raise ValueError('Invalid JPEG')
                rotation = packet.get('rotation', 0)
                if rotation == 90:
                    frame = cv2.rotate(frame, cv2.ROTATE_90_CLOCKWISE)
                elif rotation == 180:
                    frame = cv2.rotate(frame, cv2.ROTATE_180)
                elif rotation == 270:
                    frame = cv2.rotate(frame, cv2.ROTATE_90_COUNTERCLOCKWISE)
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                result = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
                atlas = extract(frame, result.face_landmarks[0], cv2, np) if result.face_landmarks else None
                print(json.dumps({'type': 'face', 'id': packet['id'], 'atlas': atlas or ''}), flush=True)
            except Exception as exc:
                print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)
        sys.exit(1)
