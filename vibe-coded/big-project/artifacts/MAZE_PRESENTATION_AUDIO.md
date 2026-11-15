# Clearer maze presentation and continuous sine audio

The observer viewport is now 192×128 color instead of an enlarged 24×18 sensor raster. It samples world geometry with wall occlusion and softened circular edges. Enemies are red, health kits have green crosses, ammo clips are gold, exits are cyan, and projectiles have bright trails. Larger health/magazine/reserve indicators sit below the view. The observer map uses the same recognizable symbols. Horizontal pixel spans are merged when recording Vulkan rectangles.

Neural vision remains the original 24×18 grayscale image. The observer renderer does not advance physics, consume RNG, modify neural pixels/audio, or expose its higher-resolution raster to either network.

The previous audio already used sine functions, but abruptly changed the frequency/amplitude of one shared oscillator. It also advanced phase before emitting the first sample and fed tiny OpenAL buffers directly from the game loop. This could produce pressure discontinuities, masked/extended events and gaps when frames arrived late.

Each event now has an independent zero-phase sine voice, a fixed sample-count duration, a smooth approximately 5 ms attack and up to 20 ms release. The first and last samples are zero. Simultaneous voices overlap and use continuous soft limiting instead of abruptly replacing one another. Note phase and envelopes continue across buffers. Pausing and closing fade active voices toward silence.

The neural audio sensor renders the new voices at 3,840 Hz. A dedicated playback thread renders the same events at a constant 48 kHz, with 10 ms buffers and about 30 ms of queued audio. Playback continues when no game frames arrive, and changing game FPS does not retune notes or resize their duration. The sensor uses simulation-time event durations; audible playback uses real-time durations. The native resolver now initializes independently of GLFW, allowing audio to be tested without opening a game window.

## Verification

- Release build: zero warnings/errors.
- Full CPU/Vulkan regression suite: **9,596 assertions passed**, with hardware Vulkan validation enabled.
- New checks verify zero-pressure endpoints, bounded sample changes, buffer-size-independent duration/waveforms, overlapping tones, fade-out, higher-resolution color landmarks, wall occlusion, and unchanged sensory/physics/RNG behavior during observer rendering.
- OpenAL silent-driver and actual-device tests both streamed continuously across a deliberate gap in game-frame submissions, with **zero errors and zero underruns**.
- Finite GLFW/Vulkan training viewer: 120 frames, continuous audio enabled on the silent test device, no validation errors. Learning averaged 20.16 ms/frame during that run; total window-loop time was 3.31 seconds. This is a functional viewer check, not a guaranteed 60 FPS benchmark.
- All viewer/test updates used temporary state or `--no-save`. SHA-256 checks confirmed the saved main model, reward model and maze state were unchanged.

Logs: `maze-presentation-validation.log`, `maze-audio-stream-validation.log`, `maze-audio-device-validation.log`, `maze-color-viewer-validation.log`.

Existing checkpoint weights still load. Audio features now reflect the improved sound synthesis, so continued training may help the learned reward translator adapt to those changed waveforms. No subjective listening-quality claim is inferred solely from the automated stream tests.
