# Nonnegative neurons and slower physics

Both CPU and Vulkan backends represent every logical quantity with opposing nonnegative neuron channels: `[max(value,0), max(-value,0)]`. Hidden channels use paired rectified tanh responses; output channels are rectified readouts. Shared/tied parameters preserve the historical decoded function. Signed weights, net potentials and gradients remain valid. Native APIs reject negative neuron inputs. JSON schema 4 and binary schema 2 declare the encoding and load legacy parameters.

Before the maze change, the full verification suite passed 9,273 assertions including historical forward-function equivalence, rate nonnegativity, native gradient behavior, inhibitory weights, frozen learning and persisted game speed. The 180-frame Intel Arc Catch smoke run measured mean 6.65 ms/frame and p95 12.33 ms; a finite GUI run also passed Vulkan validation. Those runs used `--no-save`, preserving the trained default Catch parameters.

Game movement defaults to half speed while retaining frame-by-frame learning. A quarter-speed setting is supported. The maze now uses the same nonnegative library and slower physics defaults. Its performance measurements are recorded separately in `MAZE_RESULTS.md`.
