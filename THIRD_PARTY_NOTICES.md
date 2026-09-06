<!-- Modified in the ShadowUR0 Screeny fork in 2026. -->
# Third-party notices

Screeny is distributed under Apache-2.0, but it depends on third-party components that keep their own licenses. This file documents the dependency review for the current Windows build.

## Current resolved packages

| Package family | Version used | License / terms |
| --- | --- | --- |
| LiveChartsCore / LiveChartsCore.Behaviours / LiveChartsCore.SkiaSharpView / WinUI | 2.0.0-rc5.3 | MIT |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 9.0.19 | MIT |
| SQLitePCLRaw packages | 2.1.13 | Apache-2.0 |
| SkiaSharp packages | 2.88.9 | MIT |
| HarfBuzzSharp packages | 7.3.0.3 | MIT |
| System.Drawing.Common | 8.0.4 | MIT |
| Microsoft.Win32.SystemEvents | 8.0.0 | MIT |
| System.Memory | 4.5.3 | MIT |
| Microsoft.Web.WebView2 | 1.0.2903.40 | Microsoft-provided BSD-style redistribution license |
| Microsoft.WindowsAppSDK | 1.7.250401001 | Microsoft Windows App SDK Software License Terms |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.1742 | Microsoft Windows SDK license terms; build-time package |

## Compatibility review

The Apache-2.0, MIT, and BSD-style components above permit redistribution when their notice/license conditions are followed. The Windows App SDK package contains separate Microsoft Software License Terms with a distributable-code section; Screeny uses it as part of an application with substantial primary functionality and does not redistribute the SDK as a stand-alone product.

The project keeps the original Screeny Apache-2.0 license and attribution. Release installers also include the license/notice files that are physically present in the resolved NuGet packages, collected during CI into the installed `licenses` directory.

This review is intended to keep the repository and binary distribution compliant with the licenses shipped by the current dependencies. It is not legal advice; package terms remain authoritative if they change in a future version.

## Sources

- LiveCharts2: https://github.com/Live-Charts/LiveCharts2
- Microsoft.Data.Sqlite / .NET libraries: https://github.com/dotnet/efcore and https://github.com/dotnet/runtime
- SQLitePCLRaw: https://github.com/ericsink/SQLitePCL.raw
- SkiaSharp / HarfBuzzSharp: https://github.com/mono/SkiaSharp
- Windows App SDK: https://github.com/microsoft/WindowsAppSDK
- WebView2: https://www.nuget.org/packages/Microsoft.Web.WebView2
- Windows SDK Build Tools: https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools

When changing dependency versions, rerun `dotnet list ScreenTimeTracker.csproj package --include-transitive`, review the package license metadata again, and regenerate the installer notices.
