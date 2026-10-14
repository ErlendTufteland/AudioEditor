# Audio Editor

[![CI](https://github.com/ErlendTufteland/AudioEditor/actions/workflows/ci.yml/badge.svg)](https://github.com/ErlendTufteland/AudioEditor/actions/workflows/ci.yml)

A small Windows audio editor: put several audio files one after another, trim each one, and
export the result as a new file.

## Download

Download `AudioEditor-<version>-win-x64.zip` from
[Releases](https://github.com/ErlendTufteland/AudioEditor/releases), unzip it anywhere and run
`AudioEditor.exe`. There is nothing to install: .NET is included in the exe. The only thing the
app keeps is its list of recent projects, in `%LOCALAPPDATA%\AudioEditor`.

**Requirements:** Windows 10 or 11, 64-bit (x64). FLAC, MP3 and AAC export use encoders that come
with Windows; on an N edition, add the Media Feature Pack first (*Settings* › *Apps* › *Optional
features*). Without it those formats are left out of the export dialog.

**"Windows protected your PC":** the app isn't code-signed, so SmartScreen warns about it. Click
*More info*, then *Run anyway*. To check that your download is the one published here, compare its
SHA-256 checksum with the `.sha256` file on the release page:

```powershell
Get-FileHash AudioEditor-0.1.0-win-x64.zip -Algorithm SHA256
```

## Features

- **Timeline** – add files with *Add files*, by dropping files or folders on the window, or on
  the command line (`AudioEditor.exe a.wav b.mp3`). Clips play left to right. Drag a block (or
  use Ctrl+Left/Right) to reorder. Click a block to put the playhead there.
- **Trim** – the clip editor shows the whole source file. Drag the green (start) and red (end)
  handles, type exact times (`m:ss.fff`, or seconds), or place the cursor and press **I** / **O**.
  The trimmed-away parts are shaded.
- **Preview** – *Preview from cursor* (Shift+Space) plays the whole source file, including the
  parts that are trimmed away, so you can find the right spot. Click or drag in the waveform to
  move the cursor while it plays.
- **Beat grid** – turn on *Grid* (G) to draw bars and beats over the timeline and the clip
  editor. The cursor, the trim handles and the playhead then snap to the grid (bar, beat, ½ or
  ¼ beat); hold **Alt** to place them freely. Set the tempo in the BPM box, or step it by 1 BPM
  with its arrows or Up / Down. To change the tempo part-way through, put the playhead there and
  press **+**: the tempo can then change from the nearest bar, and the BPM box sets the tempo of
  the section the playhead is in. *Tempo* lists every tempo change and sets the beats per bar and
  where bar 1 starts; in a narrow window it also holds *Snap to* and the **+** button. Typed
  times and I / O are never snapped, so you can still mark a downbeat exactly while listening.
- **Metronome** – the metronome button (or **M**) adds a click on every beat while the timeline
  or a preview plays, higher on the first beat of each bar. It follows tempo changes, can be
  switched on and off while playing, and is never exported. Set its volume under *Tempo*.
- **Projects** – *File* › *Save* (Ctrl+S) keeps the timeline in a project file (`.aeproj`): the
  clips in order with their trims, fades and volume, the beat grid with its tempo changes, and the
  last export settings. The audio isn't copied; a project refers to its files both by full path and
  relative to itself, so a folder with a project and its audio can be moved or copied as a whole.
  If files have moved anyway, opening the project asks you to find them. Open a project with
  *File* › *Open project* (Ctrl+Shift+O) or *Open recent*, by dropping it on the window, or on the
  command line (`AudioEditor.exe song.aeproj`). A `*` in the title bar means there are unsaved
  changes, and closing the window, *New project* (Ctrl+N) and opening another project ask whether
  to save them. Switching the grid, snapping or the metronome on and off is saved but isn't
  counted as a change.
- **Fades and volume** per clip (linear fade in/out, −30 to +12 dB).
- **Playback** of the finished timeline (Space to play/pause, Esc to stop, Home for the start).
- **Export** to WAV (16-bit, 24-bit or 32-bit float), FLAC, MP3 or AAC (.m4a). Clips with
  different sample rates or channel counts are converted to one format. The export is written
  to a temporary file first, so cancelling never leaves a half-written file behind.

Reads WAV, AIFF and MP3 directly, and anything Windows Media Foundation can decode
(FLAC, M4A/AAC, WMA, …). FLAC, MP3 and AAC export use the encoders built into Windows; if one is
missing (for example on Windows N editions), it is left out of the export dialog.

| Key | Action |
| --- | --- |
| Space | Play / pause the timeline |
| Shift+Space | Preview the selected file from the cursor |
| Esc | Stop (and go back to where playback started) |
| Home | Go to the start |
| I / O | Set the clip start / end at the cursor |
| G | Beat grid on / off |
| M | Metronome on / off (turns the grid on too) |
| Alt (while dragging or clicking) | Place without snapping to the grid |
| Up / Down (in the BPM box) | Tempo 1 BPM faster / slower |
| Delete | Remove the selected clip |
| Ctrl+Left / Ctrl+Right | Move the selected clip |
| Ctrl+O / Ctrl+E | Add files / export |
| Ctrl+N / Ctrl+Shift+O | New project / open a project |
| Ctrl+S / Ctrl+Shift+S | Save the project / save it under a new name |
| Scroll / Shift+scroll | Zoom / pan the clip editor |
| Ctrl+scroll | Zoom the timeline |
| Thumb wheel / middle-button drag | Pan the clip editor or the timeline |

## Build and run

Requires the .NET 10 SDK on Windows.

```bash
dotnet run --project src/AudioEditor.App
```

```bash
dotnet test
```

```bash
dotnet publish src/AudioEditor.App -c Release -o publish
```

Every push to `main` and every pull request is built and tested by
[`.github/workflows/ci.yml`](.github/workflows/ci.yml). To publish a release, push a version tag;
[`.github/workflows/release.yml`](.github/workflows/release.yml) then runs the tests, builds the
self-contained exe, and creates a GitHub Release with the zip and its checksum. Versions below 1.0,
and versions such as `1.0.0-beta.1`, are marked as pre-releases.

```bash
git tag v0.1.0
```

```bash
git push origin v0.1.0
```

## Layout

- `src/AudioEditor.Core` – audio engine without UI: decoding and waveform overview
  (`AudioSource`), trimming, gain and fades (`ClipReader`), joining and format conversion
  (`TimelineSampleProvider`), export (`AudioExporter`), the beat grid with tempo changes
  (`TempoMap`), the metronome click (`MetronomeSampleProvider`) and project files
  (`ProjectFile`). Built on [NAudio](https://github.com/naudio/NAudio) 3.
- `src/AudioEditor.App` – WPF app (Fluent theme, follows the Windows light/dark setting).
  `Controls/WaveformView` draws the waveforms and handles trimming with the mouse;
  `ViewModels/MainViewModel` holds the timeline, playback and export logic, and
  `ViewModels/BeatGridViewModel` the tempo map and snapping settings.
- `tests/AudioEditor.Core.Tests` – xUnit tests for trimming, joining, conversion, fades,
  export to every format, the tempo map, the metronome and project files.
- `tools/make-icon.cs` – draws the app icon (`src/AudioEditor.App/Assets/AudioEditor.ico`).
  After changing it, run `dotnet run tools/make-icon.cs`; pass a second path, for example
  `dotnet run tools/make-icon.cs icon.ico preview.png`, to also get a preview of every size.

## Contributing and license

Contributions are welcome as pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md). Audio Editor
is released under the [MIT License](LICENSE). It uses [NAudio](https://github.com/naudio/NAudio)
and includes .NET; their notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
