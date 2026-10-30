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

## Automatic skin colour

Each accepted portrait includes the median sRGB colour from two small cheek patches
below the eyes and above the mouth. The darkest/brightest luminance tails are discarded;
there is no fixed brightness threshold that would reject darker skin. High freezes the
colour with the same photograph. Original head materials are cloned at runtime, tinted
and made less glossy with the same fade as the portrait. Cancel/fade-out restores the
starting appearance; disabling restores the original material references and releases
clones. Other objects sharing the original material are unaffected.

HeadFaceFusion exposes Match Skin Colour, Skin Colour Strength, Skin Brightness (default
1) and Skin Smoothness (default .2). Brightness can compensate for installation lighting.
This matches the photographed colour, not illumination-independent skin reflectance;
strong coloured lighting, makeup or occluded cheeks can still bias the estimate. The
portrait's existing inward alpha feather blends its boundary into the tinted head.

## Photo proportion and placement

The worker now aligns both eye centres and the mouth centre to model-space targets
before returning the masked portrait. It no longer stretches the face's bounding box
across the entire patch. This corrects photos whose eyes appear above the model's eye
sockets and reduces the default horizontal eye spacing to 42% of the head bounds.
HeadFaceFusion > Photo Alignment exposes Photo Eye Spacing (smaller = narrower), Photo
Eye Height and Photo Mouth Height (larger = higher). Defaults are .42, .68 and .44 for
this head. Changes apply to the next sampled photo/High. These are a three-point fit;
individual nose/chin shapes and strong perspective differences still need calibration.
