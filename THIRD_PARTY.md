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

## Direct SWF writer

- Purpose: Write editable vector SWF using DefineShape3 + PlaceObject2
- Implemented directly in this project; no external SWF conversion executable is required.
- The implementation was ported/adapted from the user-provided Grok-generated source for this project.

## NetTopologySuite
- Version: 2.6.0
- License: BSD-3-Clause
- Purpose: noding/intersection handling and polygonization of connected CAD linework for closed-region fill.
- Project: https://github.com/NetTopologySuite/NetTopologySuite


## Legacy Chromium / Adobe Pepper Flash

DwgConverter에는 Chromium 또는 Adobe Flash Player/Pepper Flash 바이너리를 포함하지 않습니다.
SWF 뷰어 기능은 사용자가 `Tools\FlashViewer` 아래에 직접 제공한 로컬 바이너리만 실행합니다.
각 바이너리의 라이선스/배포 조건은 사용자가 제공한 해당 배포물에 따릅니다.
