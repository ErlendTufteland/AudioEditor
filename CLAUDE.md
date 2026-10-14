# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

A Windows audio editor: put audio files one after another on a timeline, trim them, add fades/gain, and export the result. WPF on .NET 10 (`net10.0-windows`), audio via NAudio 3.1. See README.md for the user-facing feature list and keyboard shortcuts.

## Commands

```bash
dotnet build AudioEditor.slnx                      # solution file is .slnx
dotnet run --project src/AudioEditor.App           # add file paths to open them on the timeline
dotnet test                                        # xUnit, Core only
dotnet test tests/AudioEditor.Core.Tests --filter "FullyQualifiedName~TempoMapTests.Snap_finds_the_nearest_line"
dotnet publish src/AudioEditor.App -c Release -o publish
dotnet run tools/make-icon.cs                      # regenerates src/AudioEditor.App/Assets/AudioEditor.ico
```

- A running `AudioEditor.exe` locks the build output; stop the process before rebuilding.
- `publish/` is build output and goes stale; regenerate it rather than editing it.
- No linter or formatter is configured. The build has zero warnings; keep it that way. CI builds with `-warnaserror`, so a new warning (including a NuGet vulnerability warning) fails it.
- **CI and releases** (`.github/workflows`): `ci.yml` builds and tests on pushes to `main` and on pull requests. `release.yml` runs on a `v*` tag: it calls `ci.yml`, then publishes a self-contained, compressed single-file `AudioEditor.exe` (about 72 MB; `-p:Version` comes from the tag), and attaches it zipped with LICENSE and THIRD-PARTY-NOTICES.txt, plus a `.sha256`. Versions below 1.0 and tags with a suffix (`v1.0.0-beta.1`) become pre-releases.
- `tools/make-icon.cs` is a file-based app that uses WPF, so it sets `PublishAot=false` (the file-based default enables trimming, which WPF rejects). Pass `out.ico preview.png` to also get a preview of every size; sizes up to 64 px have hand-tuned whole-pixel layouts.

## Architecture

**`src/AudioEditor.Core`** has no UI and is the only tested project. **`src/AudioEditor.App`** is a WPF MVVM app with no MVVM library: `ObservableObject` and `RelayCommand` live in `ViewModels/ObservableObject.cs`, and `RelayCommand` re-queries through `CommandManager.RequerySuggested`.

### Timeline model and the two time bases
- Clips always play back to back: no gaps, no overlaps, and reordering is by index. A clip is a source file plus `TrimStart`/`TrimEnd`, both in seconds into the source file, plus gain and fades.
- The App uses `double` seconds (`ClipViewModel`). Core uses `TimeSpan` (`ClipSpec`, made by `ClipViewModel.ToSpec()`).
- **Timeline time vs. source time:** `timeline = clip.TimelineOrigin + sourceTime`, where `TimelineOrigin = TimelineStart − TrimStart`. `MainViewModel.OnArrangementChanged` recomputes every clip's `TimelineStart` whenever clips or trims change. The beat grid, the playhead and the metronome are all in timeline time.

### Audio pipeline
- `TimelineSampleProvider` plays clips in order. It opens each file lazily on the audio thread (`ClipReader` applies trim, gain and fades) and converts everything to one rate and channel count (`WdlResamplingSampleProvider` + `ChannelMapSampleProvider`). `OutputFormat.Suggest` picks the format.
- **Playback:** `MainViewModel.StartPlayback` always wraps the provider in a `MetronomeSampleProvider`, so the click can be switched on while playing. `PlaybackService` (WaveOut) wraps it in a `GuardedSampleProvider` and disposes it off the UI thread.
- **Export:** `AudioExporter` reuses `TimelineSampleProvider` without the metronome. It writes to a temporary file, then moves it into place. FLAC/MP3/AAC use Media Foundation encoders, which can be missing; check `AudioExporter.IsAvailable`.
- **Two playback modes:**
  - `Timeline` plays the finished result.
  - `Preview` plays the selected clip's whole source file from the editor cursor, ignoring the trim.
  - The playhead is computed as `playbackOrigin + playback.Position`.
  - Any arrangement change pauses timeline playback, because the provider was built from a snapshot of the clips.

### Beat grid, snapping, metronome
- **`Core/TempoMap`** is immutable; its `With…` methods return copies. Tempo changes are only at bar starts. Beats count from bar 1, which starts at `Offset`; before it, the first tempo carries on backwards. `Snap(time, stepBeats)` assumes the step divides a bar.
- **`BeatGridViewModel`** owns the map and is replaced as a whole on every edit.
  - `ActiveMap` is null when the grid is off; this is what `WaveformView.Grid` binds to.
  - `MetronomeMap` is null unless both the grid and the metronome are on.
  - `CurrentBpm` edits the tempo section the playhead is in.
  - `TempoChangeViewModel` rows are updated in place (`SyncChanges`), so a text box that is being committed keeps its row.
- **Snapping happens in two places:**
  - `WaveformView.Snap` (clip editor handles and cursor) uses the `TimelineOrigin` captured at mouse-down (`dragOrigin`), so the grid doesn't slide while the start handle moves. The file's start and end are also snap points, and Alt bypasses snapping.
  - `MainViewModel.SeekTimeline` (clicks on the timeline) also treats the clicked clip's edges as snap points.
  - Typed times and the I/O keys are deliberately *not* snapped, so a downbeat can be marked exactly while listening.
- **Live updates:** `MetronomeSampleProvider` exposes `Map`, `Volume` and `TimelineStart`, which are safe to set from the UI thread while audio plays. `MainViewModel` pushes changes to them (`OnBeatGridChanged`, and `TimelineOrigin` changes while previewing).

### Projects (the save system)
- **File format:** `Core/ProjectFile` reads and writes `.aeproj` files: indented camelCase JSON with `format` and `version`. Files with a higher version are refused, so bump `CurrentVersion` only for changes old versions can't read; adding an optional property doesn't need a bump. `Reads_a_version_1_file` pins the format. Saving writes a temporary file and moves it into place, like export.
- **Model:** `Project` and `ProjectClip`. Times are `double` seconds, as in the App, so a time snapped to the grid is still exactly on it after a round trip. `TrimEnd` is null for "the end of the file".
- **Paths:** each clip is saved with its full path and its path relative to the project. `ProjectFile.FindClipFile` looks inside the project's folder first for files that were saved there (so a copied folder uses its own audio), and at the full path first for anything else.
- **Missing files:** on open, `MainViewModel.FindMissingFiles` asks the user to find them. Once one is found, the others are looked for in the same folder. Clips whose file is still missing are left out, which counts as a change.
- **Unsaved changes:** `UpdateIsDirty` compares `Clips.Select(c => c.ToProjectClip())` and `BeatGrid.Map` with `savedClips`/`savedMap` (the state as opened or last saved), so undoing an edit by hand clears the `*` in the title. Grid on/off, snap, metronome and export settings are saved but deliberately don't count as changes. Anything new that belongs in a project must be saved by `SaveProjectTo`, restored by `ShowProject`, and, if changing it should mark the project as changed, compared in `UpdateIsDirty`.
- **Loading:** a `ClipViewModel` made from a project keeps its `ProjectClip` until the file has decoded; `SetSource` then applies the trims and fades, kept inside the file. Until then `ToProjectClip` returns what was opened, so saving early loses nothing.
- **Flow:** `ConfirmCloseProject` asks to save before New, Open and closing the window. `OpenFilesAsync` (command line, drops, Add files) opens a project file and adds anything else. The recent list lives in `%LOCALAPPDATA%\AudioEditor\recent-projects.json` (`Services/RecentFiles`).

### WaveformView (`Controls/WaveformView.cs`)
- One control for both uses. Timeline blocks use `IsEditable=false` and show only the trimmed range. The clip editor uses `IsEditable=true` and shows the whole file with a time ruler, a bar-number lane, trim handles and a cursor.
- Rendering is cached because the playhead redraws every 30 ms during playback. The waveform is a `WriteableBitmap`; the ruler and grid are frozen `Drawing`s, each keyed by a record struct (`WaveKey`, `RulerKey`, `GridKey`). Only the cursor, handles, shading and fades are drawn each frame. Keep new visuals inside this caching scheme.
- Timeline blocks are not linear in time: each block is at least 110 px wide (`ClipViewModel.MinimumBlockWidth`), plus 10 px of chrome. Anything drawn "over the timeline" has to be drawn inside each block, as the grid is, not as a single overlay.

### UI conventions
- **Committed text boxes:** use `UpdateSourceTrigger=Explicit` with `controls:TextBoxCommit.IsEnabled="True"` (Enter or losing focus commits, Esc reverts), plus the `TimeText` (m:ss.fff), `SecondsText` or `NumberText` converters from `App.xaml`.
- **Clicking outside a text box** ends the typing even on things that don't take focus (waveform, labels): `MainWindow.OnWindowPreviewMouseDown` moves focus to the window, after clearing `FocusManager`'s remembered element (otherwise the window hands focus straight back to the box). Clicks in popups are left alone.
- **Keyboard shortcuts** are handled in `MainWindow.OnPreviewKeyDown`. Plain keys are ignored while a TextBox has focus. Ctrl shortcuts first commit the focused text box (`TextBoxCommit.Commit`), so Ctrl+S saves what is being typed; closing the window does the same.
- **Theme:** Fluent (`ThemeMode="System"`, so light or dark). The timeline and waveform areas are always dark: fixed brushes in `App.xaml` and `WaveformView`. Theme-aware brushes come from Fluent keys such as `TextFillColorSecondaryBrush`, `DividerStrokeColorDefaultBrush` and `SolidBackgroundFillColorQuarternaryBrush`.
- **Fluent TextBox width:** a Fluent TextBox shows a clear button when focused, so numeric boxes need about 84 px or their text is hidden.
- **Header width budget:** the window's `MinWidth` is 860. Below 900 px (`MainWindow.CompactHeaderWidth`) `IsHeaderCompact` moves Snap to and the add-tempo-change button from the timeline header into the Tempo popup. At 900 px the full header is nearly full in the worst case (grid on, 12 clips, playhead in a section from bar 12: 19 px to spare). New header controls need space reclaimed elsewhere, or another breakpoint.
- **Toolbar width budget:** below 1070 px (`MainWindow.CompactToolbarWidth`) `IsToolbarCompact` hides the labels of File, Add files and Remove. At 1070 px the full toolbar has 13 px to spare in the worst case (grid on, "59:59.999 / 59:59.999", "Bar 123.4"). In compact mode at 860 px there are 62 px to spare with the grid off, but with the grid on the bar/beat readout is 14 px short and slides under Export (it was 52 px short before the File button and compact mode).
- **File menu:** a `ContextMenu` on the File button, opened below it on click (`OnFileMenuClick`). The recent projects' headers are TextBlocks in a `HeaderTemplate`, so an underscore in a file name isn't read as an access key.
- **Popups:** the Tempo popup is a `ToggleButton` + `Popup`. The toggle's `IsHitTestVisible` is bound to `!Popup.IsOpen` (`InverseBool` converter) so clicking it doesn't immediately reopen the popup. Placement is custom and right-aligned (`MainWindow.PlaceUnderTempoMapButton`).
- **App icon:** comes from `<ApplicationIcon>` in the csproj; WPF windows pick it up from the exe.

## Testing and verification

- **Core tests:** xUnit, in `tests/AudioEditor.Core.Tests`. `TestAudio.WriteRamp` writes files where every sample encodes its own frame index, so trim and concatenation tests are sample-exact. `TempFolder` cleans up after each test.
- **Encoder tests:** tests that need a Windows encoder (FLAC, MP3, AAC) use `[EncoderFact(format)]` or `[EncoderData(format)]` rows. Where the encoder is missing (N editions, Windows Server without Media Foundation, possibly the CI runners) they are reported as skipped, not passed. The CI job summary lists the encoders the runner had.
- **App:** has no tests; verify UI changes by running it.
  - The exe opens files passed on the command line.
  - Controls have `AutomationProperties.Name`, and the status, position, bar/beat and clip-length text blocks have `AutomationId`s (`Status`, `Position`, `BarBeat`, `ClipLength`), so the UI can be driven and read through UI Automation.
  - While the Tempo popup is open, its text boxes come *before* the header's in the window's automation tree. Find elements by name and type, not by index.
  - Never drive it with real input (`SendInput`, `SendKeys`, `SetCursorPos`) while the user is working. When the app can't take the foreground, that input lands in their windows. Instead, run the app and the driving script on a separate desktop (`CreateDesktop`, then `CreateProcess` with `lpDesktop`), and send clicks and keys as window messages posted to the app's window.
    - Posted clicks don't press `ClickMode=Release` buttons (toggles), so use the Toggle and Invoke patterns for those.
    - Check focus with `HasKeyboardFocus`: `AutomationElement.FocusedElement` doesn't work on that desktop.
    - Text boxes inside the Tempo popup can't take focus there.
    - Posted keys carry no modifiers. For Ctrl shortcuts, attach to the app's UI thread (`AttachThreadInput`), set Ctrl down with `SetKeyboardState`, post the key, then clear it and detach. This only touches the app's input state on that desktop.
    - A UIA Invoke on a menu item that opens a modal dialog doesn't return until the dialog closes; invoke it from another thread.
    - Win32 dialogs (file dialogs, message boxes) show their controls as plain panes there. Set the file name with `WM_SETTEXT` on the Edit (AutomationId `1001` in Save, `1148` in Open), and press buttons by posting `WM_COMMAND` with the control id to the dialog (1 Save/Open/OK, 2 Cancel, 6 Yes, 7 No). The file name box appears a moment after the dialog does.
    - Opening or saving a project adds it to the real recent list in `%LOCALAPPDATA%\AudioEditor`; clean that up afterwards.
