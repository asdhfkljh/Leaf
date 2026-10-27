# Head face fusion (desktop Unity Editor prototype)

SampleScene has HeadFaceFusion on Authoritycontroller. The existing High classifier and H
preview both use the same accepted-sequence events. Camera capture warms up on Play;
High freezes a recent valid frontal face. Text movement starts the 1.5 s feature fade-in.
When the last character disappears, the result holds for 1 s and fades out over 1 s.
Low, R, disabling the controller, and a new accepted sequence cancel the previous fusion.
Missing permission, camera, model, detector, or valid face never blocks the text effect.
A face acquired after High will only be used by the next High trigger.

## Local setup (macOS Apple Silicon)

From the project root:

```sh
python3 -m venv Tools/FaceFusion/.venv
Tools/FaceFusion/.venv/bin/python -m pip install -r Tools/FaceFusion/requirements.txt
curl -fL https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task -o Tools/FaceFusion/face_landmarker.task
```

The virtual environment and model are ignored by Git. Install them on each new machine.
Python Executable defaults to that project-relative environment path. For Windows,
configure a Python executable explicitly. Python, the worker and model are external
desktop dependencies; they are not packaged into a standalone build.

Allow Unity camera access when macOS prompts. Wait for `Face fusion: local detector ready`
in Console, face the camera in good lighting, then press H. L and R cancel fusion.
The voice path triggers it automatically when all High feature rules match.
The camera is owned by Unity, not Python. JPEG frames are sampled at most twice per second,
one request in flight. PNG feature atlases return on stdout and remain in memory; no
camera images are written to disk or sent to an external service. The worker stops when
its Unity component is disabled or Play ends.

## Calibration

The Head asset has empty eye sockets. Three generated feature grids conform to the mesh,
bridging missing eye surfaces. Mesh readability is enabled to support surface sampling.
Original meshes and materials are preserved. Feature positions use camera-facing bounds
at initialization and are attached to Head, so camera movement does not slide the features.
This is a static-head prototype, not expression retargeting or identity reconstruction.

On HeadFaceFusion, Left Eye / Right Eye / Mouth are `(center x, center y, width, height)`
in normalized front-view Head bounds. Names refer to the screen's left/right. Defaults
are calibrated for this scene's front-facing camera. After changing these positions or
Head pose, restart Play to regenerate surface grids. Saturation and fade timings can be
adjusted during Play. Extreme side views, occlusion, small faces, and blurred crops are
rejected. Brightness varies with real lighting; final artistic tuning needs a real face.
The worker currently uses crop bounding boxes with feathered elliptical masks; it does
not perform full landmark triangle warping or texture color matching.

## Validation

Run `Tools/FaceFusion/.venv/bin/python -m unittest discover -s Tools/FaceFusion -p 'test_*.py'`.
These tests cover feature extraction, rejection and the actual model's no-face response.
Unity scripts and shader must also compile. Hardware acceptance: H captures once; moving
your face afterward leaves the displayed snapshot unchanged; features appear during text
travel, hold after disappearance and fade away; L/R clears them; no face plays text only;
exiting Play releases the camera and worker. Camera hardware and permissions require an
interactive Unity session.

Checked locally: all 8 Python tests passed; `pip check` passed; actual landmark inference
on MediaPipe's official portrait sample produced a valid three-region atlas; Unity C#
compilation and isolated Unity shader/feature-grid rendering passed. Live webcam permissions,
video orientation and simultaneous text/fusion playback still need an interactive hardware
acceptance run because the main project was locked by another Unity instance.
