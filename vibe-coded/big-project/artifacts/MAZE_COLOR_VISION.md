# First-person one-dimensional color vision

The neural world sensor is now a true **1D first-person RGB ray line**, not a flattened 2D overhead image. It casts **384 rays over 120 degrees** from the agent's position, ordered left to right relative to facing. Each ray reports the color of the closest intersected wall, living enemy, projectile, health kit, ammo clip or exit. Walls and closer objects occlude those behind them; things behind the field of view are not visible.

RGB light is multiplied by `1 / (1 + 0.12 * hitDistance²)`. Identical objects become darker with distance while retaining their hue. Raw ray distances are diagnostic only and are never network inputs. A 20-tile range limit returns darkness beyond range.

Each neural frame contains the 384 RGB world-ray pixels followed by 24 RGB health-status and 24 RGB ammo-status pixels. HUD brightness is screen-space and does not fade with world distance. Current and previous frames are both supplied to the main policy and reward translator, alongside stereo audio and existing controls/context. Auxiliary vision prediction now uses twelve luminance bins across the ray line.

The viewer prominently displays a tall **CURRENT VIEW** line and a smaller **PREVIOUS VIEW** line below it, with LEFT/RIGHT labels and a centre heading tick. These use the actual sensory RGB values. The small, unattenuated map on the right remains observer-only. The previous subtle flattened grayscale footer is removed.

## Checkpoint migration

Existing grayscale ports map to red channels. Zero-weight green/blue connections are added to every previous visual destination. Old stored weights, biases and plasticities retain their values and relative mappings; equal RGB copies of an old grayscale input initially reproduce its prior decoded function. Older mono/stereo and missing-context input formats remain supported. Session saves use version 4 and load versions 1–3.

The main network now has **4,133,376 stored logical connections** and **14,052 physical nonnegative rate neurons**. Both networks receive actual RGB data. Existing grayscale-trained models need continued `--train` experience to adapt to the new viewpoint, color and lighting. In `--play`, the main network learns in real time but the reward translator remains frozen, including its newly added zero-weight channels until trained.

No default checkpoint was reset or overwritten during this change. Viewer tests used `--no-save`; SHA-256 checks confirmed all three saved files were unchanged. Migrated models are saved by the normal app autosave/exit behavior.

## Verification

Release build passed without warnings/errors. The complete CPU/hardware-Vulkan suite passed **10,215 assertions**, including 31 new color/ray checks. They verify:

- Correct left/right ray ordering and exclusion of objects behind the camera.
- Wall occlusion in actual RGB sensory input.
- Darker RGB values for the same object at greater range, with hue preserved.
- Distance-independent HUD colors and finite nonnegative RGB inputs.
- Current/previous sensory history and RGB evidence reaching both networks.
- Correct predictive luminance bins across the line.
- Legacy mono/stereo migration with old weights/biases retained and unchanged functions on grayscale-replicated RGB.

A 90-frame viewer run passed Vulkan validation with the prominent first-person lines: mean learning time **17.51 ms/frame**, recent p95 **20.12 ms**, total viewer time **2.26 seconds** on Intel Arc MTL. This is not a guaranteed 60 FPS result. No retraining or gameplay-quality improvement is claimed for the changed perception.

Evidence: `maze-color-vision-validation.log`, `maze-color-vision-viewer-validation.log`, `maze-color-vision-saved.sha256`.
