# Contributing

Thanks for your interest in Beacon. Contributions are welcome: fork the
repository, make your change, and open a pull request.

## Prerequisites

- Windows 10/11 (x64)
- .NET 8 SDK (a local copy can be placed in `.tools/dotnet/`)
- Python 3 with Pillow (optional, only to regenerate the app icon)

## Build

```powershell
# Compile
.\.tools\dotnet\dotnet.exe build src\Beacon\Beacon.csproj -c Release

# Publish self-contained
.\.tools\dotnet\dotnet.exe publish src\Beacon\Beacon.csproj `
  -c Release -r win-x64 --self-contained true -o publish\Beacon

# Build Beacon-Setup.exe (also republishes and embeds the app)
powershell -ExecutionPolicy Bypass -File tools\build_installer.ps1
```

The installer is a self-contained WPF app in `src/Beacon.Installer`. Its payload zip
is a gitignored generated file — never commit it.

## Code style

- C# 12 / nullable enabled; keep names descriptive and comments minimal.
- XAML uses the existing dark theme resources in `src/Beacon/App.xaml`.
- New HTTP endpoints must go through `App.DispatchAsync` so token auth and payload
  parsing stay consistent.
- Changes to popup layout should be checked at 100%, 125% and 150% DPI and with
  more than one monitor attached.

## Testing

There is no unit-test project yet. Use the HTTP API against a local build:

```powershell
$token = (Get-Content "$env:APPDATA\Beacon\settings.json" -Raw | ConvertFrom-Json).Token
Invoke-RestMethod http://127.0.0.1:8765/health
Invoke-RestMethod http://127.0.0.1:8765/notify -Method Post `
  -Headers @{ Authorization = "Bearer $token" } `
  -Body '{"title":"Test","message":"Hello from Beacon"}' `
  -ContentType application/json
```

Useful helper scripts live in `tools/` (`inspect_ui.ps1`, `ui_actions.ps1`,
`screenshot.ps1`).

## Security

Never commit tokens, Tailscale addresses, machine names, or user-profile paths.
See [docs/SECURITY.md](docs/SECURITY.md).
