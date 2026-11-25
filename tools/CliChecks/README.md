# CLI checks

Run from the repository root on Windows:

```powershell
dotnet build Cli/NimCap.Screenshot.Cli.csproj -c Release
dotnet run --project tools/CliChecks/CliChecks.csproj -c Release -- --cli Cli/bin/Release/net10.0-windows10.0.19041.0/nimcap.exe
```

Checks the real executable's argument validation, list command JSON, output collision protection, delay deadline, unavailable window/monitor and doctor directory failures. Process-boundary checks cancel a hung worker and reject truncated image data and a missing worker executable. Doctor attempts a bounded 1x1 capture probe; the default checks save no screenshots and accept inaccessible capture backends in this negative diagnostic case.

Add `--hardware` in an interactive session with desktop capture access. It starts a temporary fixed-color WPF window, verifies actual pixels for window and rectangle captures, exercises PNG/JPEG/WebP/TIFF/BMP/PDF, default WGC/GDI fallback, relative and Unicode paths, atomic overwrite and concurrent no-overwrite output, and captures the full virtual desktop. Hardware output lives under `.artifacts/cli-checks`; no application settings or clipboard content are modified. Hardware checks fail when the desktop is inaccessible rather than reporting a false pass.

Hardware checks also verify live window metadata, case-insensitive title/process intersection, ambiguous matches, minimized targets, monitor enumeration vs captured bounds, and successful doctor probes with temporary-file cleanup. These tests require desktop access.

The release script runs the default checks against the actual self-contained published executable. Use `--cli <path>` to check a different build or extracted release.
