# Rendering The Share of the Living

`StoryMovie` is a display-independent movie application built on MYT. Its editable adaptation in `story/movie.json` follows the family story in `story/story.md` across 27 scenes, from 2029 to 2100. The complete film is about 13 minutes 40 seconds. It condenses the prose and geopolitical background; it does not automatically turn arbitrary Markdown into a screenplay.

The film aims for a restrained late-1990s prerendered look: anatomical low polygon characters with shaped torsos, jaws, noses, hair, hands and boots, textured architectural sets, camera cuts and slow camera moves. Characters use connected shoulder/elbow/wrist and hip/knee/ankle poses, breathing, speaker gestures, small mouth movements and eased collapses. Boats, waves, rain, control displays and spill gates remain animated. Dialogue appears as captions with speaker names. Narrative captions connect the time jumps. There is no synthesized speech, including in the scene about Petru's reconstructed voice.

## Requirements

- .NET 10 SDK and the engine's existing NuGet dependencies.
- FFmpeg with `libx264`, `aac`, and the `ass` filter (`libass`). A DejaVu Sans font installation is recommended; libass can substitute another available font.
- No window, Vulkan device, sound device, downloaded artwork or external audio assets are required.

Install FFmpeg with your platform's package manager or use an existing binary. Set `FFMPEG_PATH`, add FFmpeg to `PATH`, or pass `--ffmpeg /absolute/path/to/ffmpeg`.

## Render

Run commands from the repository root:

```sh
dotnet run -c Release --project src/StoryMovie -- --output renders/the-share-of-the-living.mp4
```

The default is 1280×720 at 24 fps. For a faster complete export:

```sh
dotnet run -c Release --project src/StoryMovie -- --width 960 --height 540 --output renders/the-share-of-the-living.mp4
```

A 30-second preview:

```sh
dotnet run -c Release --project src/StoryMovie -- --width 640 --height 360 --duration 30 --output renders/preview.mp4
```

A single scene (zero-based index; scene 20 is the 2074 release):

```sh
dotnet run -c Release --project src/StoryMovie -- --scene 20 --output renders/release.mp4
```

Use `--overwrite` to replace existing outputs. Ctrl+C cancels the render. The requested MP4 appears only after encoding succeeds; temporary PCM and incomplete encodes are cleaned up. Rendering is offline and may run faster or slower than playback depending on resolution and CPU.

Each export produces four files with the same basename:

| File | Contents |
| --- | --- |
| `.mp4` | H.264 video, burned-in captions, stereo AAC audio |
| `.srt` | Dialogue and narrative captions with timestamps |
| `.ass` | Styled captions, location cards and film title |
| `.json` | Render settings, scene boundaries, and story/script SHA-256 hashes |

`--duration` limits the film from its start, or from the selected scene's start when combined with `--scene`. The export rounds up to a whole frame. Original dialogue spacing is preserved; frame rate changes do not speed up the story or audio. Captions use a minimum 3.5-second beat or a longer reading interval based on word count.

A small visual preview at the intended retro scale:

```sh
dotnet run -c Release --project src/StoryMovie -- --scene 2 --width 640 --height 360 --fps 12 --duration 12 --output renders/textured-preview.mp4
```

Set construction and texture generation are procedural; no external model pack, downloaded texture, GPU, or additional package is needed. Existing MP4s must be rendered again to show these changes. The export remains an offline CPU render, so start with a short preview before rendering the full film.

## Edit the film

Edit `story/movie.json` to change the adaptation. Each scene has `title`, `location`, `set`, `action`, `cast`, and `beats`. A beat contains a `speaker` and `text`; an empty speaker marks narrative text. `source` resolves relative to the script file. Story source is preserved and hashed for traceability; changes to the prose require a corresponding adaptation edit.

Supported sets: `harbor`, `cooling`, `grid`, `ship`, `hospital`, `flood`, `conference`, `workshop`, `home`, `control`, `gate`, `hearing`, `settlement`.

Supported actions: `arrival`, `refusal`, `radio`, `work`, `sailing`, `birth`, `rescue`, `debate`, `grief`, `planning`, `repair`, `release`, `loss`, `testimony`, `transmit`, `future`, `ending`.

Cast names: `Leila`, `Father`, `Tomas`, `Arun`, `Petru`, `Mara`, `Samira`, `Officer`, `Ina`. Speaker labels may also identify people heard over radio or system text; they do not create voices.

The action selects choreography and sound cues; the set selects geometry. To add new blocking, props, or choreography, edit `src/StoryMovie/Stage.cs`. Sound cues are in `Soundtrack.cs`. The animation is stylized and uses kinematic posing rather than a physical flood simulation or motion capture. This is an original procedural interpretation of that period’s look, not a reproduction of StarCraft artwork or its cinematic production quality.

## Engine abstractions

`MYT.Kit.Audio.IAudioSource` evaluates stereo samples on an explicit clock. `SoundCue` supplies deterministic wind, water, rain, electrical hum, radio static, beeps, impacts, pumps and bells. `AudioMix` applies a bounded stereo mix and can compile cues into time buckets. `WaveAudio` writes 16-bit, 48 kHz stereo PCM with bounded memory. The movie's original procedural score is another `IAudioSource`.

`MYT.Kit.Media.OfflineVoxelRenderer` retains `Grid3D<uint>` for architectural masses, then draws shaped cross-section meshes and smooth low polygon ellipsoids for people, pipes, boat hulls and props. Its software material shaders sample deterministic 64×64 texture tiles for woven cloth, skin, hair, wood grain, plaster, stone and worn metal. Glass and water receive restrained specular highlights; water normals animate with the timeline. A filtered directional shadow map, depth based contact shading, cool ambient fill, warm key light, distance haze and gentle highlight compression add depth. Orthographic composition preserves the old prerendered aesthetic. Draw calls queue geometry; `Finish` resolves shadows, shading and RGBA pixels. It is a CPU rasterizer added to the engine for offline rendering. The existing Vulkan game renderer is separate; this application does not capture the game's window.

`FfmpegMovieWriter` streams RGBA to an FFmpeg process with argument-list escaping, drains stderr, burns styled captions, encodes the WAV audio as AAC, and writes an MP4 with fast-start metadata. All frame and sample times come from the same fixed timeline. Memory use does not grow with the number of frames; intermediate PCM requires about 158 MB for the complete film and is deleted afterward.

## Verification

```sh
dotnet run -c Release --project src/StoryMovie -- --validate
dotnet run -c Release --project src/StoryMovie -- --self-test
```

Validation checks the adaptation, story source and scene selection. The self-test exercises subtitle coverage and clipping, continuous scheduling, every set, reproducible and changing animation frames, voxel face culling, depth ordering, curved mesh visibility, material differences, texture variation, cast shadows, cue boundaries, stereo panning, bounded audio, and WAV sample counts. It requires no FFmpeg or GPU. An actual preview export additionally verifies FFmpeg availability, filters and codecs.

With FFprobe installed, inspect an export:

```sh
ffprobe -v error -show_streams -show_format renders/the-share-of-the-living.mp4
```
