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

## Installation Via NuGet:

```bash
Install-Package SimpleAudioPlayer
```

## Quick Start
```csharp
// Create player instance var player = new AudioPlayer();
// Use file stream (local path)
player.ChangeHandler(new FileStreamHandle("song.mp3"));
// Get total duration TimeSpan duration = player.GetDuration();
// Playback controls
player.Play();
player.Stop();
player.Pause();
// Progress operations
var currentTime = player.GetTime();
player.Seek(TimeSpan.FromSeconds(30));
```

## Stream Handlers 
| Handler Type | Description |
|-----------------------|------------------------------|
| `FileStreamHandle` | Local file stream |
| `HttpStreamHandle` | HTTP network stream |
| `StreamHandle` | Generic stream (requires Stream object) |
| `CustomHandle` | Fully customizable implementation |

## Dependencies
- Audio playback via [miniaudio](https://github.com/mackron/miniaudio)
- Audio decoding via [FFmpeg](https://ffmpeg.org/)
- Native component: [SimpleAudioPlayer.Native](https://github.com/j4587698/SimpleAudioPlayer.Native) (LGPL-2.1+)

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
