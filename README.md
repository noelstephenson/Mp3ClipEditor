# MP3 Clip Editor

Windows desktop application for creating MP3 clips from full songs, previewing edits in real time, and managing artist/title ID3 tags with filename-aware tools.

## What It Does

- Load multiple MP3 files into a batch queue
- Select a track on demand and display its waveform
- Set clip start and end positions visually
- Move the full clip window while keeping its current length
- Preview the full track or only the selected clip
- Apply fade in, fade out, gain, and optional normalization
- Edit artist/title tags directly in the queue
- Export the active clip or export all queued tracks
- Parse source filenames into artist/title values
- Bulk rename source files to `Artist - Title.mp3`
- Write queue artist/title values back to source MP3 ID3 tags

## Main Layout

### Left side

- `Load MP3 Files`
  - Add MP3 files to the queue
  - Remove the active track
  - Reset the full queue
- `Track Defaults`
  - Default clip duration
  - Default fade in
  - Default fade out
  - Apply defaults to queued tracks
- `Track Export Status`
  - Export progress bar
  - Export all queued tracks to a selected folder
- `Batch Queue`
  - Shows all loaded tracks
  - Lets you edit `Artist` and `Title` directly
  - Displays duration, clip length, audio load state, normalization, and ID3 settings

### Right side

- `Active Track`
  - Waveform view
  - Readouts for start, end, clip length, current position, fade in, fade out, and gain
  - Sliders for fade in, fade out, and gain
  - Playback and save controls
- `Bulk Renamer`
  - Choose a filename pattern for parsing
  - Apply filename parsing to queue tags
  - Write queue tags back to source MP3 files
  - Rename source files to `Artist - Title.mp3`

## Waveform Controls

- Drag the left or right bottom marker to change clip start or end
- Click and drag inside the selected region to move the whole clip window
- Click inside the selected region without dragging to start playback from that point
- While preview is playing:
  - the start handle cannot move past the current play position
  - the full clip selection cannot be dragged past the current play position

## File Naming and Tagging

The app is designed around the preferred output format:

`Artist - Title.mp3`

The bulk renamer can interpret several existing filename styles, including:

- `Artist - Title`
- `Title - Artist`
- `Artist, Title`
- `Title, Artist`
- `Artist.Title`
- `Title.Artist`
- `Artist_Title`
- `Title_Artist`
- `Title Only`

Regardless of input style, the output naming target remains:

`Artist - Title.mp3`

## Playback and Processing

- Preview playback uses `NAudio` for in-app audio playback
- Export and waveform decoding use bundled `FFmpeg` tools
- Source-file tag writing uses `TagLibSharp`

## Notes

- Audio is not fully loaded for every queued file up front
- Waveform/sample data is loaded on demand when a track is selected
- Local sample audio files are excluded from git by `.gitignore`

## Build

```powershell
dotnet build
```

## Repository

GitHub repository:

`https://github.com/noelstephenson/Mp3ClipEditor`
