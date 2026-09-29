# DK DWG To IMG/SWF Converter v0.4.0

DWG 모델 공간을 SVG, PNG, 편집 가능한 벡터 SWF로 변환하는 Windows .NET 10 WinForms 도구입니다. 실행 파일명은 `DK-DWG-To-IMG-SWF-Converter.exe`입니다.

## 현재 변환 경로

### SVG / PNG

```text
DWG
 -> ACadSharp.Image
 -> raw SVG
 -> SvgStyleProcessor
 -> SVG 저장 / Svg.Skia PNG 렌더링
```

지원 설정:

- 작업 공간 너비/높이
- 내용 최대 너비/높이와 종횡비 유지
- 좌/중/우, 상/중/하 정렬
- 투명/지정 배경
- 선 색/두께 일괄 지정
- 닫힌 영역 채우기
- raw SVG 캐시
- 대형 SVG 저장 단계 heartbeat 로그

### 일반 크기 SWF

일반 크기 도면은 SVG/PNG와 동일하게 스타일 적용된 SVG를 만든 뒤 프로젝트 내부 Writer로 SWF를 생성합니다.

```text
DWG
 -> ACadSharp.Image
 -> styled SVG
 -> SvgDirectSwfExporter
 -> DirectSwfWriter
 -> DefineShape3 + PlaceObject2 SWF
```

외부 `svg2swf.exe`는 사용하지 않습니다.

### 대용량 SWF 경로

SWF가 선택되어 있고 아래 조건 중 하나에 해당하면 중간 SVG를 만들지 않는 CAD 직접 SWF 경로를 사용합니다.

- 파일 크기 100 MB 이상
- 모델 공간 엔티티 500,000개 이상

```text
DWG
 -> ACadSharp entities
 -> CadDirectSwfExporter
 -> DirectSwfWriter
 -> SWF
```

이 경로는 메모리 사용을 줄이기 위한 보조 경로입니다. 복잡한 도면에서는 결과가 손상될 수 있으며 품질을 보장하지 않습니다. 대용량 엔진 최적화는 현재 보류 상태입니다.

## 복잡도 사전 점검

DWG를 읽은 뒤 렌더링 전에 파일 크기와 모델 공간 엔티티 수를 기준으로 위험도를 판정합니다.

- 주의: 50 MB 이상 또는 300,000 엔티티 이상
- 고위험: 100 MB 이상 또는 500,000 엔티티 이상

Hatch, Insert, Spline 개수도 함께 표시합니다. 고위험 도면에서는 선택한 출력 계속, SWF만 시도, 파일 건너뛰기 중 하나를 선택할 수 있습니다.

## SWF 보기

SWF 생성 자체에는 Flash가 필요하지 않습니다.

프로그램의 선택적 `SWF 보기` 기능은 Chromium 53.0.2785.0 x86과 x86 Pepper Flash를 사용합니다.

```text
Tools\FlashViewer\Chromium\chrome.exe
Tools\FlashViewer\PepperFlash\pepflashplayer.dll
Tools\FlashViewer\PepperFlash\manifest.json   # 선택
```

- Chromium은 프로그램의 `Chromium 다운로드` 버튼으로 GitHub Release에서 내려받아 설치할 수 있습니다.
- 배포하는 Chromium은 공식 snapshot revision `403380`의 Chromium 53.0.2785.0 x86입니다.
- Chromium ZIP은 SHA-256 `66ddd4f54b5bbb21ee87eba2beed9677f2edbe87592372ca74ed2d42f6c4bce2`를 확인한 뒤 설치합니다.
- Pepper Flash는 패키지에 포함하지 않으며 사용자가 x86 `pepflashplayer.dll`을 직접 넣어야 합니다.
- Chromium/Pepper Flash 바이너리는 기본 프로그램 배포물에 포함하지 않습니다.

## 주요 파일

- `CadConversionPipeline.cs`: 변환 흐름과 출력 경로 선택
- `SvgStyleProcessor.cs`: SVG 스타일/배치 처리
- `SvgDirectSwfExporter.cs`: styled SVG -> SWF shape 변환
- `CadDirectSwfExporter.cs`: 대용량 도면용 CAD 직접 SWF 변환
- `DirectSwfWriter.cs`: DefineShape3 기반 SWF writer
- `DrawingRiskAssessment.cs`: 대용량/고복잡도 사전 위험 판정
- `LegacyFlashViewer.cs`: 선택적 로컬 SWF 확인 기능

## 빌드 / 배포

소스 빌드에는 Windows용 .NET 10 SDK가 필요합니다.

일반 Release 빌드:

```bat
build.bat
```

win-x64 배포:

```bat
publish-win-x64.bat
```

배포 방식은 **Framework-dependent**입니다.

- .NET Runtime은 배포물에 포함하지 않습니다.
- 실행 PC에는 **.NET 10 Desktop Runtime (x64)** 이 설치되어 있어야 합니다.
- 출력 위치: `publish\win-x64`
- 단일 EXE가 아니므로 배포할 때는 `publish\win-x64` 폴더의 파일을 함께 배포해야 합니다.
- 배포 스크립트는 이전 self-contained 파일이 남지 않도록 출력 폴더를 비운 뒤 새로 publish합니다.
- 배포에 불필요한 `.pdb` 디버그 심볼은 생성/복사하지 않으며, 패키지에서 따라오는 PDB도 publish 후 제거합니다.

## 릴리스 자동화

정식 프로그램 Release는 GitHub Actions가 자동 생성합니다.

- Pull Request에서는 `release-win-x64.ps1`로 실제 배포 ZIP 생성까지 검증하고 Release는 만들지 않습니다.
- `main`에 병합되면 프로젝트의 `Version`을 읽어 `vX.Y.Z` Release 존재 여부를 확인합니다.
- 같은 버전 Release가 이미 있으면 중복 배포하지 않습니다.
- 새 버전이면 Windows runner에서 Framework-dependent win-x64 배포를 새로 생성합니다.
- 프로그램 ZIP과 SHA-256 파일을 GitHub Release Asset으로 게시합니다.
- Chromium/Pepper Flash 바이너리는 프로그램 Release ZIP에 포함하지 않습니다.
- 수동 실행이 필요하면 GitHub Actions의 `Release` workflow를 실행할 수 있습니다.

새 정식 버전 배포 흐름은 **버전 변경 PR → CI 검증 → main 병합 → 자동 Release 생성**입니다.

