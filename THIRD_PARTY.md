# Third-party components

This file lists third-party components used by or distributed separately for this project.
The project's MIT License covers only code and materials authored for this repository unless otherwise noted.
Third-party components remain subject to their own licenses.

## ACadSharp.Image

- Purpose: DWG/DXF parsing and CAD rendering to SVG
- Package: ACadSharp.Image 0.1.6
- License: MIT
- Project: https://github.com/slaveOftime/ACadSharp.Image

## Svg.Skia

- Purpose: Rasterize the styled SVG scene to PNG
- Package: Svg.Skia 5.2.3
- License: MIT
- Project: https://github.com/wieslawsoltes/Svg.Skia

## NetTopologySuite

- Purpose: Noding/intersection handling and polygonization of connected CAD linework for closed-region fill
- Package: NetTopologySuite 2.6.0
- License: BSD-3-Clause
- Project: https://github.com/NetTopologySuite/NetTopologySuite

## Direct SWF writer

- Purpose: Write editable vector SWF using DefineShape3 + PlaceObject2
- Implemented directly in this project; no external SWF conversion executable is required.
- The implementation was ported/adapted from an earlier user-provided generated prototype used during development. The historical prototype source is not part of the active project.

## Optional legacy Chromium / Adobe Pepper Flash viewer

The main DK DWG To IMG/SWF Converter package does not include Chromium or Adobe Flash Player / Pepper Flash binaries.

An optional Chromium 53.0.2785.0 x86 runtime is distributed separately as a GitHub Release asset for the SWF viewer.
It is based on official Chromium snapshot revision 403380 and is not Google Chrome.

- Project: https://www.chromium.org/
- Snapshot source: https://storage.googleapis.com/chromium-browser-snapshots/Win/403380/chrome-win32.zip
- Chromium code is distributed under a BSD-style license, with bundled third-party components subject to their respective licenses.

Adobe Pepper Flash is not distributed by this project.
Users must supply their own x86 `pepflashplayer.dll` under `Tools\FlashViewer\PepperFlash`.
