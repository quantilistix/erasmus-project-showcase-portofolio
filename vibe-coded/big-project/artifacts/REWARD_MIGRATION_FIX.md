# Release training crash: checkpoint error-state migration

The existing default checkpoint (`paired-error-pc-v2`, frame 904794, ten completed mazes) reproduced the crash in Release training on both CPU and Vulkan. Both files contained finite learned weights and activities. The main error population reached 11,372.574 and the reward error population 53,485.51. Resuming those old accumulated error integrals under the new motor boundaries and positive main plasticity produced nonfinite activity in `MazeSession.StepControls`, reported by `RateEncoding.Validate`.

The previous validation used fresh small graphs and missed this long-trained migration case.

Checkpoints without `ErrorStateVersion` now rebase their main and reward error populations to zero once on load. Learned weights, normal activities, maze progression, counters, reward traces and existing motor parameters are retained. New saves record `ErrorStateVersion: 1`; subsequent resumes preserve both populations exactly. The loader still validates the old state before migration, rather than hiding nonfinite/corrupt data. A startup message identifies the migration. No predictive equations, learning rates or motor rule are changed.

Unexpected runtime exceptions no longer trigger the final save. Normal shutdown, Escape and Ctrl+C still save. This avoids trying to replace a valid checkpoint with the failed runtime state.

Regression checks cover pre-fix V1, V2 and V3 files with large accumulated errors, unchanged learned weights/normal activities/progression/counters, actual training after migration, motor persistence, and exact error-state restoration after saving the migrated experiment. The complete suite passed 17,669 assertions. Release build passed without warnings.

Logs:

- `reward-migration-crash-before.log`: failure on the real checkpoint using Vulkan.
- `reward-migration-cpu-before.log`: independent CPU reproduction.
- `reward-migration-cpu-after.log`: 120 Release CPU training frames after migration.
- `reward-migration-vulkan-after.log`: 2,000 Release Vulkan training frames on the same original checkpoint, ending at frame 906794 with finite activities.
- `reward-migration-save.log` and `reward-migration-resume.log`: migration/save/resume using a temporary copy.
- `reward-migration-validation.log`: complete verification suite.

Original checkpoint files were used with `--no-save`; their SHA-256 hashes were checked unchanged. Save/resume checks wrote only to a temporary copy. Vulkan in this environment is llvmpipe software rendering on CPU.

The final resumed Vulkan run completed all 2,000 frames in 243.93 seconds without a crash. The CPU run completed 120 frames. An intentional failed graphical launch (`DISPLAY=invalid`) also confirmed that exception shutdown leaves all three checkpoint files unchanged by SHA-256; its expected GLFW startup error is recorded in `reward-migration-failed-launch.log`.
