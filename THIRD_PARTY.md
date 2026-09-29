# Third-party components

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

DwgConverter does not include Chromium or Adobe Flash Player / Pepper Flash binaries.

The optional local SWF viewer runs only user-supplied binaries placed under `Tools\FlashViewer`.
Licensing and redistribution terms for those binaries remain the responsibility of the supplied distribution.
