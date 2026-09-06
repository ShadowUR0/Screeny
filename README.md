# Screeny

A privacy-focused screen time tracker for Windows, built with WinUI 3 and the Windows App SDK.

## About

Screeny helps you understand how much time you spend in desktop applications without sending your activity history to a remote service. It tracks the foreground application, stores finalized usage slices locally, and presents the data in a compact activity view inspired by digital-wellbeing dashboards.

### Key features

- **Privacy first** — usage history stays on your device
- **Native Windows 11 UI** — WinUI 3, Windows App SDK and Mica
- **Low background overhead** — event-driven foreground tracking with throttled UI work
- **Local analytics** — current usage, app totals, charts and historical date selection
- **Tray operation** — tracking continues while the dashboard is hidden
- **Idle awareness** — away time is separated from application screen time

## Installation

The upstream Screeny application is available through the Microsoft Store. This fork is under active development; test builds for the redesign are produced by the Windows GitHub Actions workflow on pull requests.

## Usage

1. Launch Screeny
2. Tracking starts automatically
3. Use the activity screen to view total screen time and per-application usage
4. Select another date to inspect historical activity
5. Close the dashboard to keep Screeny running from the tray

## Building from source

### Prerequisites

- Windows 11
- .NET SDK 8.0 or newer
- Windows App SDK development prerequisites

### Build

```powershell
git clone https://github.com/ShadowUR0/Screeny.git
cd Screeny
dotnet restore ScreenTimeTracker.sln
dotnet build ScreenTimeTracker.sln -c Release -p:AppxPackageSigningEnabled=false
```

## Privacy

Screeny does not add telemetry, analytics, cloud synchronization, or remote activity storage. Usage data is stored locally in the application's SQLite database. See [PRIVACY.md](PRIVACY.md) for the upstream privacy policy.

## License

This project is licensed under the Apache License 2.0. See [LICENSE.md](LICENSE.md).
