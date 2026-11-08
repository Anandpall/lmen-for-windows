# Lmen v2 (Lumen for Windows)

**Ambient, audio-reactive screen-edge lighting and timed lyrics for Windows.**

Lmen v2 turns whatever is playing on your PC into light around the edges of your display. It reads the current track from Windows media controls, extracts a palette from the album artwork, and renders an animated neon border that moves with the music. A fullscreen visualizer, a click-through desktop overlay, motion album artwork, and word-synced lyrics are built in.

Inspired by the macOS ambient player **Lumen**, rebuilt from scratch for Windows.

## Features

### Ambient edge lighting
- Multi-stop glowing border that hugs the display edges and corners.
- Album-art palette extraction with vibrance enhancement and a grayscale fallback.
- Rotating light, breathing animation, adjustable brightness, spread, and backdrop bloom.
- Audio-reactive pulse driven by WASAPI loopback capture.
- Built-in color presets and a screen color dropper for per-track custom colors.

### Fullscreen visualizer
- Large artwork, blurred cover backdrop, and a dark ambient stage.
- Play/pause, previous/next, shuffle, repeat, seek bar, and master volume.
- Windowed or borderless fullscreen, with taskbar auto-reveal on hover.
- Fullscreen motion mode that plays animated album covers and canvas loops.

### Desktop overlay
- Transparent, click-through edge glow that stays visible while you browse, work, or game.
- Taskbar-aware z-order handling.

### Timed lyrics
- Word- and syllable-level sync from TTML and QRC, with Enhanced LRC and plain LRC fallback.
- Two display styles: **Stage** (BitChord-inspired) and **Fisheye Lens** (Verci-inspired).
- Live lyric translation, romanization, manual sync offset, and a per-song provider picker.
- Instrumental and plain-text lyric support.
- Provider chain with disk caching and in-flight deduplication:
  LrcRed, BiniLyrics, BetterLyrics, PaxSenix, LyricsPlus, Unison, KuGou, LRCLIB,
  Musixmatch, and a Genius plain-text fallback.

### Motion artwork
- Animated covers from Apple Music editorial/album videos, Tidal video covers, and the
  Vivi community canvas library.
- Background prefetch so playback starts immediately, plus an on-disk cache.

### Media and system integration
- Windows Global System Media Transport Controls (GSMTC): works with Spotify Desktop,
  Spotify Web, YouTube Music in Chrome/Edge, Apple Music, Tidal, VLC, and local players.
- Studio-quality artwork lookup from iTunes and YouTube Music, with palette re-extraction.
- System tray companion with fullscreen, overlay, settings, and exit actions.
- Single-instance handling, per-monitor DPI awareness, and optional high-performance GPU preference.
- Settings apply in real time and persist to disk.

## Requirements

- Windows 10 version 1809 (build 17763) or newer. Windows 11 recommended.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build and run from source.
- A media app that publishes to Windows media controls. No account, login, or API key is required.
- Optional: a PaxSenix API key for that provider's extended routes.

## Run from source

```bash
git clone https://github.com/Anandpall/lmen-for-windows.git
cd lmen-for-windows
dotnet run --project LmenV2.csproj
```

You can also double-click `Run-LmenV2.bat`.

## Build a release

```bash
dotnet publish LmenV2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published app is written under `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Space` | Play / pause |
| `Left` / `Right` | Previous / next track |
| `Up` / `Down` | Volume up / down |
| `L` | Open or close timed lyrics |
| `S` | Switch Stage / Fisheye lyrics style (while lyrics are open) |
| `M` / `F` | Toggle fullscreen motion artwork (when available) |
| `M` | Mute (when motion artwork is unavailable) |
| `F11` | Toggle windowed / fullscreen |
| `Esc` | Close the current layer; from the player, switch to the desktop overlay |

Double-click the album art to enter fullscreen motion mode.

## Settings and local data

| Path | Contents |
|---|---|
| `%APPDATA%\LmenV2\settings.json` | Glow, audio, and lyrics settings |
| `%LOCALAPPDATA%\LmenV2\Lyrics` | Cached lyric provider responses |
| `%LOCALAPPDATA%\LmenV2\LyricsTranslation` | Cached translations and romanizations |
| `%LOCALAPPDATA%\Lmen\CanvasCache` | Downloaded motion artwork |

Settings include glow brightness and spread, rotation speed, breathing amount, backdrop brightness, music reactivity, lyric beat effects, translation language, lyric offset, and an optional PaxSenix API key.

## Project layout

| Path | Purpose |
|---|---|
| `App.xaml.cs` | Startup, single-instance handling, window and tray lifecycle |
| `Rendering/` | Edge beam geometry, glow profiles, backdrop blur |
| `Services/` | Media sessions, audio capture, color extraction, artwork, lyrics, settings |
| `Views/` | Fullscreen visualizer, desktop overlay, and settings windows |
| `Tray/` | System tray icon and menu |
| `Models/` | Track metadata and lyric timing models |
| `Helpers/` | Win32 interop |

## Tech stack

- .NET 8, WPF, and WinForms (tray icon)
- NAudio.Wasapi for loopback audio capture
- Windows.Media.Control for media session detection
- Custom color quantization and palette extraction
- TTML, QRC, Enhanced LRC, and LRC lyric parsers
- Win32 interop for click-through overlays and taskbar handling

## Credits

- Inspired by **Lumen (lmen)** for macOS.
- Lyric presentation inspired by **BitChord** (Stage) and **Verci** (Fisheye Lens).
- Built as an open, experimental Windows counterpart.
