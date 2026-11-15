# Maze shooter implementation and verification

The app now exposes only `--train` and `--play`. Both update the main policy every active frame. Training enables ideal reward labels and supervised reward translation; play freezes the translator and uses its predicted pleasure/pain plus primitive sensations. The game does not calculate ideal visit/event labels during play. Old Catch/Dodge/Rally source and checkpoints remain available internally as regression fixtures; their old CLI commands are no longer the app interface.

## Implemented world

Full episodes use a connected procedural 19×19 maze with loops, an exit, six moving enemies, six ammo clips and six health kits. The planar egocentric 24×18 viewport rotates with facing direction and occludes objects behind walls. Its bottom two rows encode health/ammunition visually. A full map exists only in the observer UI. Raw PCM and FFT include footsteps and combat/pickup sounds.

Player and enemy projectiles travel through the world; collision substeps prevent tunnelling. Enemy aim, shot timing and projectile speed vary. Magazine capacity is eight; reloading requires time and reserve ammunition. Physics defaults to half speed, independent of the per-frame learning target.

Primitive signals come from healing, damage and missing health. Ideal nonprimitive targets are +0.2/hit, an additional +0.6/kill, +0.25/ammo and +1/exit, with positive labels capped at 1. Revisited cells produce pain growing by 0.04 per repeat to a cap of 1; dwelling also counts every 60 physics-time units. These labels teach both the main action values and reward translator only during training.

## Saved starter run

A fresh maze model was trained on Intel Arc MTL for 6,000 frames, then continued for 1,200 frames after the final context-update correction. The shared checkpoint is `artifacts/maze/`:

- Frames, main updates and reward updates: **7,200 each**.
- Experience collected: **9 hits, 3 kills, 8 ammo pickups, 8 healing pickups and 1 completed training maze**.
- Network: **3,199,488 stored connections**, **10,404 nonnegative rate neurons**, eight hidden layers of 512 logical units.
- Initial training: mean **15.59 ms/frame**, recent p95 **16.36 ms**.
- Final 1,200-frame continuation: mean **15.80 ms/frame**, recent p95 **16.46 ms** (about **63.3 compute updates/s**).

The first 12,000 training frames use short connected teaching layouts. The saved starter is still in that curriculum; it has not demonstrated mastery of full procedural mazes. Continue with `--train`; play always starts in a full maze. The checkpoint is saved with reward training enabled; loading `--play` freezes it before any frame, and saving from play persists that frozen state. Returning to `--train` explicitly re-enables reward learning.

Logs: `maze-training.log`, `maze-training-continuation.log`.

## Verification

Release build passed without warnings. The complete suite passed **9,580 assertions**, including 307 maze-specific checks. Coverage includes wall collision/occlusion, travelling bullets, damage/kills, timed reloads, ammo and health pickups, increasing visit penalties, reachable procedural exits, nonnegative rates, native GPU learning, mode isolation and save/resume. The broader suite also covers numerical gradients, fixed plasticity and CPU/Vulkan agreement.

The final 180-frame full-maze play run used Vulkan validation and `--no-save`:

- Main updates advanced **7,200 → 7,380**.
- Reward updates remained **7,200**.
- Mean **10.99 ms/frame**, recent p95 **14.71 ms** (about **91 compute updates/s**).
- Saved file checksums remained unchanged.

A finite GLFW/Vulkan viewer run also passed validation. It used `--no-save` and did not overwrite the starter checkpoint. Play/update counts in that log predate the final continuation.

Logs: `maze-validation.log`, `maze-play-validation.log`, `maze-viewer-validation.log`; saved-file digest evidence: `maze-play-saved-before.sha256`.

## Limits

Event collection is evidence of the training pipeline, not proof of learning over a random policy. No held-out navigation or reward-classification success rate is claimed. The translator has seen few rare events, and short-context revisit inference is difficult under partial vision. Frozen reward predictions can be wrong or exploited, especially as main-network probe representations change. Training has no scripted oracle controller; persistent random exploration and learned action values generate experience. Further training and evaluation are needed before treating the agent as a capable maze player.
