# 3DDFA identity reconstruction for Unity

SampleScene and Test now default to **Dense3DDFA** on HeadFaceFusion. Unity captures
camera images; MediaPipe localizes a face; official 3DDFA_V2 MobileNet ONNX estimates
40 identity coefficients on CPU. These drive the SAME BFM basis in Unity, eliminating
the old heuristic mapping from two measured proportions to an unrelated head.
Pose and expression estimates are used for inference/crop refinement only, not applied
to the avatar. Camera photographs are not used as textures.

## Run on this Mac

The local model files and ONNX Runtime have been prepared. In Unity:

1. Open SampleScene or Test. Stop any old Play session before reloading changed scripts.
2. Optionally run **Tools > Leaf > Validate 3DDFA Face Mapping**. It independently compares
   Python's reference geometry with the actual baked Unity BlendShapes and checks reset.
3. Enter Play and allow camera access. Wait for `3DDFA: CPU reconstruction ready`.
4. Face the camera in good lighting with a relaxed expression for three stable samples,
   then press H or finish a voice recording classified High. Text travel starts the
   1.5-second identity morph; after text disappears, hold 1 second and fade back over 1 second.
5. L/R cancels. No valid recent/stable measurements means text only, with a Console reason.
   `Identity Strength` controls the proportion of the estimated shape applied (default 1).

The visible object under Head is `3DDFA matched identity face`, with 80 paired identity
BlendShapes (40 positive/negative coefficient pairs). The original Head renderers are
retained and restored when this component/Play stops. Anchors and the existing High events
are reused. The BFM template is a FACE surface: it has no complete back-of-head, neck or
separate eyeballs. This is intentional for similarity evaluation, not a finished full head.

## Install on another machine

```sh
python3 -m venv Tools/FaceFusion/.venv
Tools/FaceFusion/.venv/bin/python -m pip install -r Tools/FaceFusion/requirements.txt
curl -fL https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task -o Tools/FaceFusion/face_landmarker.task
Tools/FaceFusion/.venv/bin/python Tools/FaceFusion/prepare_3ddfa.py
```

Preparation downloads files from official cleardusk/3DDFA_V2 commit
`1b6c67601abffc1e9f248b291708aef0e43b55ae` and the official pre-converted ONNX download.
An existing official ONNX file can be passed with `--onnx /absolute/path/to/mb1_120x120.onnx`;
a local upstream checkout can be passed with `--source /absolute/path/to/3DDFA_V2`.
The verified ONNX checksum is enforced. All derived geometry, basis data, validation data,
and inference files live under ignored `Tools/FaceFusion/3ddfa-data/`. They are not in Git.
Python and models remain external desktop dependencies, not a packaged standalone player.

**License:** upstream 3DDFA_V2 code is MIT (attribution in THIRD_PARTY_3DDFA_LICENSE.txt).
The supplied modified BFM2009 model is **academic-use only**. The preparation step preserves
its upstream notice. Code licensing does not grant permission for commercial BFM use.
See https://github.com/cleardusk/3DDFA_V2/blob/master/bfm/readme.md for model terms.

## How the mapping works

`prepare_3ddfa.py` exports BFM's mean mesh and 40 shape basis vectors. Each full positive
or negative BlendShape represents three parameter-standard-deviations in one identity
direction. `tddfa_backend.py` predicts coefficients using the official input preprocessing,
parameter normalization, and second-pass landmark crop. It excludes expression from the
reconstructed avatar and clamps standardized shape coefficients to [-3,3]. Unity converts
those same coefficients to paired weights without landmark-to-mesh retargeting.

The local worker and Unity template exchange a model/basis signature. A mismatch prevents
morphing instead of silently applying coefficients to the wrong topology. Three fresh
frames are median-filtered; excessive coefficient spread prevents capture. Identity is
frozen once per accepted High sequence, so subsequent movement cannot change that sequence.
The old `Maximum Shape Weight` and ratio reference controls apply only to LegacyProportions.
The 3DDFA sample interval defaults to .15 s; this is sampling cadence, not a measured end-to-end
frame-rate guarantee. Pose, occlusion, lighting and the model prior still limit likeness.

Images stay in process memory and are passed only to the local worker; no face frames are
saved or uploaded. Model downloads use the network, but image inference does not. Disable
or exit Play to release the camera and process.

## Verification and fallback

```sh
Tools/FaceFusion/.venv/bin/python -m unittest discover -s Tools/FaceFusion -p 'test_*.py' -v
Tools/FaceFusion/.venv/bin/python -m pip check
```

15 tests cover legacy measurements, crop padding, malformed/no-face recovery, actual ONNX
initialization, invalid coefficients, and Python-vs-exported-template geometry. Unity's
mapping menu checks all 38,365 vertices against Python and validates reset. Independent
Unity rendering/baking also checks the actual runtime renderer. Official neutral portrait
inference returned valid 40-dimensional shape data; its measured 3DDFA two-pass time was
about 11 ms on this Mac, excluding MediaPipe, camera capture, communication and rendering.
Live camera orientation, perceived likeness, and simultaneous voice/text/morph playback
still require interactive acceptance in the user's already-open Unity project.

To compare the previous implementation, select **Legacy Proportions** in the component's
Backend field and restart Play. Its original mesh asset is preserved. See
[LEGACY_BLENDSHAPES.md](LEGACY_BLENDSHAPES.md) for that workflow.
