<!-- Modified in the ShadowUR0 Screeny fork in 2026. -->
<p align="center">
  <img src="Assets/screeny.svg" width="92" alt="Screeny icon">
</p>

<h1 align="center">Screeny</h1>

<p align="center">
  A private, native screen-time tracker for Windows 11.
</p>

<p align="center">
  <a href="https://shadowur0.github.io/Screeny/">
    <img alt="Download Screeny" src="https://img.shields.io/badge/Download-Screeny-16c7d9?style=for-the-badge&logo=windows11&logoColor=white">
  </a>
  <a href="LICENSE.md">
    <img alt="Apache 2.0" src="https://img.shields.io/badge/License-Apache%202.0-5865F2?style=for-the-badge">
  </a>
</p>

Screeny tracks the foreground application, keeps finalized usage history on your PC, and presents it in a compact activity view inspired by digital-wellbeing dashboards. The fork focuses on a cleaner interface, lower background overhead, reliable app icons, and normal Windows update/install behavior.

## Highlights
- **Local by design** — Screeny itself adds no telemetry, analytics, cloud sync, or remote activity storage
- **Native Windows UI** — WinUI 3, Windows App SDK, Mica, and a desktop-friendly activity layout
- **Low background overhead** — event-driven foreground tracking, reduced timer wake-ups, and no unnecessary media polling while active
- **Efficient when hidden** — chart and dashboard refresh work pauses while Screeny is in the tray; tracking continues
- **Reliable app icons** — executable paths are retained for history, icons are cached, deduplicated, and lazy-loaded only for visible rows
- **Accurate local history** — finalized usage slices are stored in SQLite; idle/away time is excluded from app totals
- **Responsive charts** — live chart refreshes are throttled without making the interface feel stale
- **Single instance** — opening Screeny again activates the existing instance instead of starting a second tracker
- **In-place updates** — the installer upgrades the existing installation instead of creating duplicate copies

## Download

<p align="center">
  <a href="https://shadowur0.github.io/Screeny/"><strong>Open the Screeny download page →</strong></a>
</p>

Current development release: **1.8.2** · **Windows 11** · **x64**

Use `Screeny-Setup.exe` for normal installation. Future installers use the same application identity and install location, so newer versions replace the existing Screeny installation. Running Screeny is asked to shut down cleanly during an upgrade.

## How it works

1. Launch Screeny; tracking starts automatically
2. Screeny records the foreground desktop application locally
3. The activity screen shows total screen time, an hourly/daily chart, and per-app usage
4. Historical dates and ranges are read from the local SQLite history
5. Closing the dashboard keeps tracking active from the tray

## Building from source

### Requirements

- Windows 11
- .NET SDK 8.0 or newer
- Windows App SDK development prerequisites

```powershell
git clone https://github.com/ShadowUR0/Screeny.git
cd Screeny
dotnet restore ScreenTimeTracker.sln
dotnet build ScreenTimeTracker.sln -c Release -p:AppxPackageSigningEnabled=false
```

The Windows CI also builds the upgradeable Inno Setup installer and audits resolved NuGet packages for known vulnerabilities.

## Privacy

Usage history is stored under the user's local application data in Screeny's SQLite database. The application does not add an account system, advertising SDK, telemetry endpoint, cloud synchronization, or remote usage-history service. See [PRIVACY.md](PRIVACY.md).

## License and attribution

Screeny is a modified fork of the original Screeny project by **Arno Gevorkyan**. The original project is licensed under the **Apache License 2.0**, and this fork keeps that license and the original copyright notice intact.

See [LICENSE.md](LICENSE.md), [NOTICE](NOTICE), and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for attribution and dependency licensing information. Installer builds also collect the license/notice files shipped by resolved NuGet dependencies.
