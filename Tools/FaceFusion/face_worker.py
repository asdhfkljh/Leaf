"""Local face proportions for authored Unity BlendShapes. Never returns camera textures."""
import argparse
import base64
import json
import sys
from pathlib import Path


def measure(landmarks, width, height, np):
    """Use relaxed frontal frames; normalize distances so camera distance/roll do not change identity."""
    p = np.array([(v.x * width, v.y * height) for v in landmarks], dtype=np.float64)
    if p.shape != (478, 2) or not np.isfinite(p).all():
        return None
    distance = lambda a, b: float(np.linalg.norm(p[a] - p[b]))
    face_width = distance(234, 454)
    outer_span = distance(33, 263)
    if face_width < 120 or outer_span < 65:
        return None
    # Reject clipped contours and strong roll/yaw, rather than deforming Head from perspective distortion.
    for index in [234, 454, 10, 152, 33, 263, 61, 291]:
        if not (0.02 * width < p[index, 0] < .98 * width and .02 * height < p[index, 1] < .98 * height):
            return None
    eye_mid = (p[33] + p[263]) / 2
    if abs(float(p[33, 1] - p[263, 1])) > outer_span * .18:
        return None
    if abs(float(p[1, 0] - eye_mid[0])) > outer_span * .18:
        return None
    mouth_mid = (p[61] + p[291]) / 2
    nose_drop = float(p[1, 1] - eye_mid[1]) / outer_span
    if not (.12 < nose_drop < .70) or mouth_mid[1] <= p[1, 1]:
        return None
    mouth_width = distance(61, 291)
    if mouth_width < 30 or distance(13, 14) / mouth_width > .12:
        return None  # Open mouth is expression, not identity.
    if distance(159, 145) / max(distance(33, 133), 1) < .12:
        return None
    if distance(386, 374) / max(distance(263, 362), 1) < .12:
        return None
    eye_left = (p[33] + p[133]) / 2
    eye_right = (p[263] + p[362]) / 2
    eye_ratio = float(np.linalg.norm(eye_right - eye_left)) / face_width
    mouth_ratio = mouth_width / face_width
    if not (.2 < eye_ratio < .8 and .2 < mouth_ratio < .8):
        return None
    return {'eye_spacing_ratio': eye_ratio, 'mouth_width_ratio': mouth_ratio}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--backend', choices=['mediapipe', '3ddfa'], default='mediapipe')
    args = parser.parse_args()
    engine = None
    if args.backend == '3ddfa':
        from tddfa_backend import TDDFAEngine
        engine = TDDFAEngine()
    import cv2
    import mediapipe as mp
    import numpy as np
    options = mp.tasks.vision.FaceLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(Path(__file__).with_name('face_landmarker.task'))),
        num_faces=1, min_face_detection_confidence=.7, min_face_presence_confidence=.7)
    with mp.tasks.vision.FaceLandmarker.create_from_options(options) as detector:
        print(json.dumps({'type': 'ready', 'backend': args.backend, 'model_id': engine.model_id if engine else ''}), flush=True)
        for line in sys.stdin:
            try:
                packet = json.loads(line)
                raw = np.frombuffer(base64.b64decode(packet['image']), dtype=np.uint8)
                frame = cv2.imdecode(raw, cv2.IMREAD_COLOR)
                if frame is None:
                    raise ValueError('Invalid JPEG')
                rotation = packet.get('rotation', 0)
                if rotation in [90, 180, 270]:
                    frame = cv2.rotate(frame, {90: cv2.ROTATE_90_CLOCKWISE, 180: cv2.ROTATE_180,
                                              270: cv2.ROTATE_90_COUNTERCLOCKWISE}[rotation])
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                result = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
                ratios = None
                if result.face_landmarks:
                    ratios = engine.infer(frame, result.face_landmarks[0]) if engine else measure(result.face_landmarks[0], frame.shape[1], frame.shape[0], np)
                reply = {'type': 'face', 'id': packet['id'], 'valid': ratios is not None}
                if ratios:
                    reply.update(ratios)
                print(json.dumps(reply), flush=True)
            except Exception as exc:
                print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)
        sys.exit(1)
