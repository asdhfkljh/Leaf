"""Local face proportions for authored Unity BlendShapes. Returns a masked face portrait in memory for High reveals."""
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


def portrait(landmarks, frame, np, cv2):
    """Return an upright, contour-masked PNG; reject clipped/small or strongly turned faces."""
    h, w = frame.shape[:2]
    p = np.array([(v.x*w, v.y*h) for v in landmarks])
    if p.shape != (478, 2) or not np.isfinite(p).all(): return None
    indices = [10,338,297,332,284,251,389,356,454,323,361,288,397,365,379,378,400,377,152,148,176,149,150,136,172,58,132,93,234,127,162,21,54,103,67,109]
    contour = p[indices]
    lo, hi = contour.min(axis=0), contour.max(axis=0)
    span = np.linalg.norm(p[33]-p[263])
    if hi[0]-lo[0] < 100 or span < 50: return None
    if np.any(lo < [2,2]) or np.any(hi >= [w-2,h-2]): return None
    if abs(p[33,1]-p[263,1]) > span*.18: return None
    if abs(p[1,0]-(p[33,0]+p[263,0])/2) > span*.22: return None
    x0,y0 = np.floor(lo).astype(int); x1,y1 = np.ceil(hi).astype(int)
    crop = frame[y0:y1+1,x0:x1+1]
    mask = np.zeros(crop.shape[:2], np.uint8)
    cv2.fillPoly(mask, [np.round(contour-[x0,y0]).astype(np.int32)], 255)
    # Inward feather keeps background outside the contour fully transparent.
    alpha = np.clip(cv2.distanceTransform(mask, cv2.DIST_L2, 3)/max(3,(x1-x0)*.045),0,1)
    rgba = cv2.cvtColor(crop, cv2.COLOR_BGR2BGRA); rgba[:,:,3] = (alpha*255).astype(np.uint8)
    rgba = cv2.resize(rgba, (384,512), interpolation=cv2.INTER_AREA)
    ok, encoded = cv2.imencode('.png', rgba)
    return base64.b64encode(encoded).decode() if ok else None


def main():
    import cv2
    import mediapipe as mp
    import numpy as np
    options = mp.tasks.vision.FaceLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(Path(__file__).with_name('face_landmarker.task'))),
        num_faces=1, min_face_detection_confidence=.7, min_face_presence_confidence=.7)
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
                if rotation in [90, 180, 270]:
                    frame = cv2.rotate(frame, {90: cv2.ROTATE_90_CLOCKWISE, 180: cv2.ROTATE_180,
                                              270: cv2.ROTATE_90_COUNTERCLOCKWISE}[rotation])
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                result = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
                ratios = measure(result.face_landmarks[0], frame.shape[1], frame.shape[0], np) if result.face_landmarks else None
                reply = {'type': 'face', 'id': packet['id'], 'valid': ratios is not None}
                if ratios:
                    reply.update(ratios)
                if result.face_landmarks:
                    image = portrait(result.face_landmarks[0], frame, np, cv2)
                    if image: reply['portrait'] = image
                print(json.dumps(reply), flush=True)
            except Exception as exc:
                print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        print(json.dumps({'type': 'error', 'message': str(exc)}), flush=True)
        sys.exit(1)
