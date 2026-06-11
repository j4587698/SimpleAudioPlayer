# SimpleAudioPlayer
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT) 

[中文版本](README-zh.md)

A simple cross-platform audio playback library with **SimpleAudioPlayer.Native (LGPL-2.1+)** backend, supporting multiple audio formats and streaming protocols. 
## Features 
- 🎵 Common audio formats support (via FFmpeg decoding)
- 📁 Multi-protocol handling: local files, HTTP streams, custom streams
- ⏯️ Basic playback controls: Play/Stop/Pause/Seek
- ⏲️ Track duration and progress monitoring
- 🔧 Extensible stream handling system (custom data sources)

## What's New in 2.0
- Native dependency updated to **SimpleAudioPlayer.Native 2.0.0**.
- Playback failures now surface through `PlaybackFailed` and `PlaybackState.Error`.
- HTTP stream handlers report I/O failures instead of silently treating broken streams as EOF.
- `ProgressiveHttpStreamHandle` supports play-while-downloading to a final local file.
- `DiskCachedStreamHandle` caches streamed data on disk to avoid holding large files in memory.

## Installation Via NuGet:

```bash
Install-Package SimpleAudioPlayer -Version 2.0.0
```

## Quick Start
```csharp
// Create player instance 
var player = new AudioPlayer();
// Use file stream (local path)
player.Load(new FileStreamHandle("song.mp3"));
// Get total duration TimeSpan 
var duration = player.GetDuration();
// Playback controls
player.Play();
player.Stop();
player.Pause();
// Progress operations
var currentTime = player.GetTime();
player.Seek(30);
```

## Error Handling
```csharp
player.PlaybackStateChanged = state =>
{
    Console.WriteLine($"Playback state: {state}");
};

player.PlaybackFailed = args =>
{
    Console.WriteLine($"Playback failed: {args.Result}");
    Console.WriteLine(args.Exception);
};
```

## Progressive HTTP Download
```csharp
var handle = await ProgressiveHttpStreamHandle.CreateAsync(
    "https://example.com/song.mp3",
    "song.mp3",
    overwrite: true);

handle.ProgressChanged += (downloaded, total) =>
{
    Console.WriteLine($"{downloaded}/{total}");
};

handle.DownloadStateChanged += (_, args) =>
{
    Console.WriteLine($"Download state: {args.State}");
};

player.Load(handle);
player.Play();
```

## Stream Handlers 
| Handler Type | Description |
|-----------------------|------------------------------|
| `FileStreamHandle` | Local file stream |
| `HttpStreamHandle` | HTTP network stream |
| `StreamHandle` | Generic stream (requires Stream object) |
| `CustomHandle` | Fully customizable implementation |
| `CachedStreamHandle` | Caching support for network streams |
| `DiskCachedStreamHandle` | Disk-backed cache for large streams |
| `ProgressiveHttpStreamHandle` | HTTP play-while-downloading with a final local file |

## Dependencies
- Audio playback via [miniaudio](https://github.com/mackron/miniaudio)
- Audio decoding via [FFmpeg](https://ffmpeg.org/)
- Native component: [SimpleAudioPlayer.Native 2.0.0](https://github.com/j4587698/SimpleAudioPlayer.Native) (LGPL-2.1+)

## License - Main project: **[MIT License](LICENSE)** 
- Native component: **[LGPL-2.1+](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)**
  **Important Compliance Notice:**
  When distributing software using this library, you MUST:
  - Provide access to LGPL-licensed component's source code
  - Allow end-users to replace the LGPL component
  - Include full license texts

## Contributing We welcome issues and pull requests! Please include: 
- Steps to reproduce issues
- Relevant logs/error messages
- Environment details (OS/.NET version etc.)

## License 
![license](https://img.shields.io/github/license/j4587698/SimpleAudioPlayer)
