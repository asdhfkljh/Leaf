# Head identity morph (Unity BlendShapes)

High now changes the actual Head geometry, preserving the source white material.
No cropped face atlas, photographic overlay, or facial feature patch mesh is used.
The first version supports eye spacing and mouth width, each with wide/narrow targets.
It approximates these proportions; it does not reconstruct a person's complete identity.

## Play in SampleScene

1. Enter Play and allow Unity camera access. Wait for `Head morph: local detector ready`.
2. Face the camera with eyes open and a relaxed, closed mouth for about two seconds.
3. Press H (or finish a recording classified High). The controller freezes the median
   of three fresh, stable measurements. When text travels toward the ear, shapes fade
   in over 1.5 seconds. After text disappears, they hold for 1 second, then return over
   1 second. L/R cancels immediately. Missing/invalid measurements play text only.
4. During Play, expand the Head hierarchy and select `Head BlendShape renderer`.
   Its SkinnedMeshRenderer exposes EyeSpacingWide, EyeSpacingNarrow, MouthWide,
   MouthNarrow. Sliders can be checked individually from 0 to 100. Do not set both
   directions of the same feature simultaneously. The next sequence overwrites manual
   slider changes. Source Head geometry and materials are restored when Play ends.

The existing High voice rules and H preview use the same four sequence events.
This retains the original Head object/anchors. At runtime a SkinnedMeshRenderer using
an authored mesh replaces rendering of the original MeshRenderer; the source FBX is
not overwritten. BlendShapes move the original topology's vertices and normals.
Empty eye sockets remain empty; this task does not add eyeballs or expression animation.

## Assets and tuning

`Assets/Models/HeadMorph.asset` contains the four persistent BlendShapes. The editor
command **Tools > Leaf > Rebuild Head BlendShapes** reproducibly authors these shapes
from Assets/head.fbx using smooth anatomical influence regions. It preserves the mesh
asset GUID. The builder is calibrated to this source model's size, orientation and
anatomical regions; changing to another model requires recalibration. These are
procedurally authored shape targets, not artist-sculpted identity reconstruction.

On HeadFaceFusion, Reference Eye Spacing (.52) and Reference Mouth Width (.42) are
adjustable artistic reference ratios for this Head, not universal human measurements.
The detector measures eye-center distance / cheek width and mouth-corner distance /
cheek width. Ratios above/below reference select opposite shapes. Full shape controls
represent roughly +/-20% eye-center movement and +/-25% local mouth-width scaling,
with smooth boundaries, not a guarantee of exact measured final dimensions.
Maximum Shape Weight defaults to 75 to keep changes moderate. Verify proportions on
several relaxed frontal faces before choosing a final installation calibration.

HeadFaceFusion requires three valid measurements with ratio spread <= .035 and oldest
sample age <= 2 s. Keep Maximum Snapshot Age longer than the sampling window if changing
sample count/interval. Closed eyes, open mouth, significant roll/yaw, clipped/small faces,
and invalid numbers are rejected to avoid mistaking expression/perspective for identity.
These gates are heuristic; an ordinary webcam cannot accurately recover full 3D shape.

## Local detector setup (macOS Apple Silicon)

From the project root:

```sh
python3 -m venv Tools/FaceFusion/.venv
Tools/FaceFusion/.venv/bin/python -m pip install -r Tools/FaceFusion/requirements.txt
curl -fL https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task -o Tools/FaceFusion/face_landmarker.task
```

Python Executable defaults to the project-relative virtual environment above. Model and
venv are ignored by Git and must be installed separately on other machines. This remains
a desktop Editor prototype; Python/model are not packaged in a standalone build.
Unity owns the camera and sends upright JPEGs to a persistent local MediaPipe process.
Only valid numeric proportions return. Images are not saved or uploaded to a service.
Exit Play or disable the component to release the camera and process.

## Verification

Run `Tools/FaceFusion/.venv/bin/python -m unittest discover -s Tools/FaceFusion -p 'test_*.py' -v`.
Tests cover normalized measurements, camera-distance/translation/roll invariance,
expression/pose rejection, malformed images and actual-model no-face recovery.
The MediaPipe official business-person sample was also verified to return valid numeric
ratios without an atlas. **Tools > Leaf > Validate Head BlendShapes** checks four nonempty
shape frames, actual baked vertex displacement,
paired weight selection/clamping, preservation of material, and rendered wide/narrow
comparisons. Live camera authorization and synchronized voice/text/morph playback still
need an interactive hardware run in the user's already-open Unity project.
