# High-triggered face photograph reveal

Enter Play in SampleScene, Test, or Test 1 and allow webcam access. The original head, ears, neck, materials and silhouette remain visible. A photo patch
is sampled against the actual front surface, following nose and cheek depth. Only
enclosed eye openings are bridged so the photographic eyes remain visible. Wait for `Face reveal: local detector ready`.
Face the camera, then finish a High voice recording or press H. The latest valid local
portrait (at most two seconds old) is frozen and fades onto a curved face patch over
1.5 seconds. After the High text finishes, it holds one second and fades out over one
second. L/R cancels immediately. Exit Play removes the photo overlay.

The detector crops along MediaPipe's face oval and feathers alpha inward to remove
background. Small, clipped, strongly rolled/turned faces are rejected. Talking is allowed.
No-face or stale measurements keep the original head; the text sequence still plays.
This is a photographic face effect, not 3D identity reconstruction. The overlay is calibrated for this project's +Z-facing head. Face Horizontal Range
and Face Vertical Range control its placement within the head bounds. The existing authored BlendShape assets remain available but are unused
by this effect. The High snapshot is independent of subsequent camera frames.

## Local setup (macOS Apple Silicon)

```sh
python3 -m venv Tools/FaceFusion/.venv
Tools/FaceFusion/.venv/bin/python -m pip install -r Tools/FaceFusion/requirements.txt
curl -fL https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task -o Tools/FaceFusion/face_landmarker.task
```

Unity owns the camera and sends JPEGs to a persistent local Python process. Masked PNGs
return through standard output. Photos remain in memory, are not saved or uploaded,
and textures/process/camera are released when disabled. Python and model are ignored by
Git. This is a desktop Editor prototype: standalone distribution needs separate Python
and model packaging. The shader is in Resources so Unity retains it.

## Verification

Run `Tools/FaceFusion/.venv/bin/python -m unittest discover -s Tools/FaceFusion -p 'test_*.py' -v`.
Legacy proportion tests remain for the retained measurement helper. Portrait tests cover
alpha masking, invalid/clipped faces and talking. Live webcam framing and voice timing
must also be checked in Play on the installation hardware.
