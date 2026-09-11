# 🌟 Lmen for Windows (Lumn)

> **Ambient screen-edge lighting, real-time audio dynamics, and synchronized lyrics companion for Windows.**

---

## 💡 The Story & Inspiration

I saw **Lumen (lmen)** running on macOS and was immediately captivated by how clean and immersive it felt. The way ambient light hugged the screen edges while listening to music was simply magical.

As a Windows user, I looked for an equivalent and realized there wasn't one that felt right. So I decided: *why not build it myself?*

> ⚠️ **Project Status — Polishing Phase**  
> This project is currently in an active **polishing phase**. The core engine, audio reactivity, color extraction, and lyrics are functioning, while edge glow smoothness, light rotation, and UI subtleties are being continuously tuned and refined.

> ⚡ **100% Vibecoded**  
> This entire app was **vibecoded as a fun little weekend project**! Built through rapid flow state, high-tempo iteration, and AI pair programming to turn an idea seen on Mac into a living Windows app in days.

---

## ✨ Features

- 🌈 **Screen-Edge Ambient Glow**: Smooth, blurred radial border glow around your display that adapts in real-time to your music.
- 🎵 **Audio Reactive Dynamics**: High-performance WASAPI loopback audio capture with real-time FFT spectrum analysis. Bass hits and beat drops pulse through the glow.
- 🎨 **Adaptive Color Extraction**: Extracts dominant, vibrant palette hues directly from current album artwork.
- 🎤 **Synchronized Floating Lyrics**: Floating karaoke-style synced lyrics view with automatic smooth scrolling.
- 🪟 **Windows-Native Controls**: Sleek, transparent dark-mode UI with native Windows window management (minimize, close to system tray, settings popover).
- 🎧 **Multi-Source Support**: Interacts with Windows System Media Transport Controls (Spotify, YouTube Music via Chrome, browser players, local media players).

---

## 🛠️ Tech Stack

- **Framework**: .NET 8 / WPF (Windows Presentation Foundation)
- **Audio Engine**: NAudio (WASAPI loopback audio stream capture & FFT analysis)
- **Color Analysis**: Custom k-means / palette extraction from album art
- **Lyrics Engine**: Synced LRC parser and web lyric providers
- **Platform Interop**: Win32 API (`User32`, `DwmApi`) for borderless click-through overlay and transparent backdrop rendering

---

## 🚀 Getting Started

### Prerequisites
- Windows 10 (version 1903+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher

### Quick Run
Clone the repository and double-click `Run-LumnWindows.bat`, or build and run from the terminal:

```bash
# Clone the repository
git clone https://github.com/Anandpall/lmen-for-windows.git
cd lmen-for-windows

# Build and run
dotnet run --project LumnWindows/LumnWindows.csproj
```

---

## 📸 Media & Demo

A demo preview recording (`lumn-demo.mp4`) is included in this repository demonstrating the ambient edge lighting and floating lyrics in action.

---

## 🙏 Credits & Acknowledgments

- Inspired by the original **Lumen (lmen)** concept on macOS.
- Built as an open, experimental Windows counterpart.
- Developed with passion as a weekend vibecoding project.
