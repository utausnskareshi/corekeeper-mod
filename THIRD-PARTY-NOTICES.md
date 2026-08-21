# Third-party notices

This application is distributed as a self-contained build, so the libraries below are shipped
inside the released package. Each is used under its own licence, and those licences require the
copyright and permission notice to travel with the redistributed binaries.

This file lists what the released `cks-gui` / `cks` executables actually contain, as reported by
the package metadata of the resolved dependency graph.

## MIT License

The following packages are distributed under the MIT License. The MIT permission notice is
reproduced in `LICENSE` in this repository and applies equally to each of them, with copyright
held by the respective authors.

| Package | Version | Author |
|---------|---------|--------|
| Avalonia | 12.1.1 | The Avalonia Project |
| Avalonia.BuildServices | 11.3.2 | The Avalonia Project |
| Avalonia.Desktop | 12.1.1 | The Avalonia Project |
| Avalonia.Fonts.Inter | 12.1.1 | The Avalonia Project |
| Avalonia.FreeDesktop | 12.1.1 | The Avalonia Project |
| Avalonia.FreeDesktop.AtSpi | 12.1.1 | The Avalonia Project |
| Avalonia.HarfBuzz | 12.1.1 | The Avalonia Project |
| Avalonia.Native | 12.1.1 | The Avalonia Project |
| Avalonia.Remote.Protocol | 12.1.1 | The Avalonia Project |
| Avalonia.Skia | 12.1.1 | The Avalonia Project |
| Avalonia.Themes.Fluent | 12.1.1 | The Avalonia Project |
| Avalonia.Win32 | 12.1.1 | The Avalonia Project |
| Avalonia.X11 | 12.1.1 | The Avalonia Project |
| CommunityToolkit.Mvvm | 8.4.2 | .NET Foundation and Contributors |
| HarfBuzzSharp | 8.3.1.3 | Microsoft Corporation |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | Microsoft Corporation |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | Microsoft Corporation |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | Microsoft Corporation |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | Microsoft Corporation |
| MicroCom.Runtime | 0.11.6 | The Avalonia Project |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.0 | Microsoft Corporation |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | Microsoft Corporation |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | Microsoft Corporation |
| SkiaSharp | 3.119.4 | Microsoft Corporation |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | Microsoft Corporation |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | Microsoft Corporation |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | Microsoft Corporation |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | Microsoft Corporation |
| System.IO.Pipelines | 8.0.0 | Microsoft Corporation |
| Tmds.DBus.Protocol | 0.94.1 | Tom Deseyn |

## Bundled native components

Some of the packages above carry native libraries built from other projects, which keep their
own upstream licences:

- **Skia**, redistributed inside the `SkiaSharp.NativeAssets.*` packages — BSD 3-Clause,
  Copyright (c) Google Inc.
- **HarfBuzz**, redistributed inside the `HarfBuzzSharp.NativeAssets.*` packages — the
  "Old MIT" licence used by the HarfBuzz project.
- **ANGLE**, redistributed inside `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419 — BSD
  3-Clause, Copyright (c) The ANGLE Project Authors. This package declares its licence as a
  bundled `LICENSE` file rather than an SPDX identifier; see the file inside the package.

## Fonts

- **Inter**, embedded through `Avalonia.Fonts.Inter` — SIL Open Font License 1.1,
  Copyright (c) The Inter Project Authors.

## Development-only dependencies

These are not part of the released package and are listed for completeness:

- `AvaloniaUI.DiagnosticsSupport` 2.2.3 — referenced only for Debug builds; excluded from
  Release through `IncludeAssets`/`PrivateAssets` in `src/gui/CoreKeeperSkinTool.Gui.csproj`.
- `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` — test
  projects only.

## Not redistributed

The mod itself is shipped as C# source and is compiled at run time by Core Keeper's own bundled
compiler. It builds against APIs that the game provides and this project does not distribute:

- **PugMod** — Core Keeper's official modding API, supplied by the game.
- **HarmonyX / Lib.Harmony** — supplied by the game's mod loader.
- **Unity engine assemblies** — supplied by the game.

Core Keeper itself, its assets and its trademarks belong to Pugstorm. This project contains no
game assets. What `data/` holds instead is measurements and recipes: `player-body-layout.json`
and `player-parts.json` record coordinates and dimensions measured from the game and nothing
else, and `presets.json` lists colours and shape choices written for this project. The artwork
the tool shows is drawn from those numbers at run time; no pixel of the game's own is stored
here or redistributed.

## How this list was produced

The package list comes from the resolved dependency graph of the GUI project
(`src/gui/obj/project.assets.json`), with each licence read from the corresponding `.nuspec` in
the local NuGet cache. Re-check it after changing any dependency version.
