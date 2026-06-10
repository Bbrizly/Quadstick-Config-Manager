# QuadStick Config Manager

[![CI](https://github.com/Bbrizly/Quadstick-Config-Manager/actions/workflows/build.yml/badge.svg)](https://github.com/Bbrizly/Quadstick-Config-Manager/actions/workflows/build.yml)

A Windows desktop app for editing [QuadStick](https://www.quadstick.com/) controller profiles. The QuadStick is a mouth-operated game controller used by quadriplegic gamers; this tool gives its profiles a real configuration UI instead of the device's built-in audio menus.

## Features

- Edit a player's full profile on screen.
- Import and export profile data as CSV, with starter templates.
- Serial bridge for talking to the device.
- Multi-screen, guided edit workflow.

## Tech

C# · .NET 9 · WPF · CommunityToolkit.Mvvm · CsvHelper

## Build & run

```bash
dotnet restore QuadStickConfigManager/QuadStickConfigManager.csproj
dotnet build   QuadStickConfigManager/QuadStickConfigManager.csproj -c Release
dotnet run --project QuadStickConfigManager/QuadStickConfigManager.csproj
```

## CI/CD

Every push and pull request is built on `windows-latest` via GitHub Actions (`.github/workflows/build.yml`): restore, build, test, and publish a self-contained single-file Windows executable as a downloadable artifact. Pushing a `v*` tag cuts a GitHub Release with the packaged build attached.
