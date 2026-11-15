# Larger procedural mazes and missed-shot reward training

Both train and play now generate **41×41** mazes (1,681 cells, about 4.7 times the earlier 19×19 area). A randomized depth-first carving pass creates connected corridors; additional connected rooms and loops provide junctions and alternate paths. A new episode/reset generates a new layout. Equal seeds still reproduce the same generation sequence.

The exit is placed at the furthest reachable floor by shortest path. The episode time budget is `max(3600, exitPathLength * 40)` physics-time units to allow longer routes, turning, combat and detours. Enemies, ammo and health scale with walkable area. Early training no longer uses a fixed 9×9 layout: it uses the same large generator, with fewer/weaker enemies and nearby health/ammo opportunities until frame 12,000. The standard encounter population is used afterward and throughout play. Neural input/output shapes are unchanged.

## Missed-shot supervision

A player projectile that hits a wall or reaches its lifetime limit without hitting an enemy causes **0.15 learned pain**. Each resolved projectile is counted once. Shots still in flight, successful hits, enemy projectiles and attempts to fire an empty magazine do not count. Multiple misses add to any revisit penalty and saturate at a total pain target of 1.

Misses generate an audible impact as perceptual evidence. They do not generate primitive pain or alter health. Training supplies the penalty to policy learning and as a reward-translator label. A dedicated rare-miss replay pool helps preserve these sparse examples. Play continues to freeze the translator and derives its learning signal from predicted pleasure/pain plus primitive sensations; no ideal miss labels are fed into play updates.

## Training performed

No game process was running when training began. The previous checkpoint was backed up under `artifacts/maze/before-large-maze-misses/`.

The saved checkpoint continued from **7,200 → 13,200 frames**, with **6,000 additional policy updates and 6,000 additional reward-network updates**. Training collected **46 missed player projectiles**. The first generated large training maze had eight enemies and a 119-tile shortest exit route. Training crossed the 12,000-frame boundary and switched to the standard encounter population.

Intel Arc MTL: mean **15.76 ms/learning frame**, recent p95 **18.06 ms**, total **96.00 seconds**. Current files are saved in `artifacts/maze/{main.ngb,reward.json,maze.json}`. Normal `--play` loads this trained checkpoint and freezes its reward head. The backup retains the earlier progress.

This demonstrates that the new supervision was used for training, not a held-out guarantee that the translator recognizes every miss or that shooting accuracy improved. Continued experience is still needed on the larger maps.

## Verification

Release build completed with zero warnings/errors. **10,014 assertions passed** with hardware Vulkan validation. Across twelve seeds in both modes, checks cover connected floor cells, sealed borders, exactly one reachable furthest exit, loops, scaled resources, deterministic seeds and new layouts after reset. Combat checks cover wall misses, range-expiration misses, one penalty per projectile, no penalties on successful hits or dry fire, and strict training/play reward separation. Existing neural, audio and graphics checks also pass.

The larger overview exposed excessive per-rectangle Vulkan command overhead. The renderer now composes into a reusable mapped framebuffer and uploads the complete image once per frame on compatible swapchains; the existing render-pass path remains a fallback. The final 60-frame large-maze viewer check passed Vulkan validation in **1.54 seconds**, with learning averaging **14.84 ms/frame**. Saved checkpoint checksums remained unchanged.

Logs: `maze-large-misses-training.log`, `maze-large-misses-validation.log`, `maze-large-misses-viewer-validation.log`. Viewer verification uses `--no-save`; `maze-large-misses-saved.sha256` records the trained files for preservation checks.
