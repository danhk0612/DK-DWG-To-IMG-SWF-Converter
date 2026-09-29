# DwgConverter v0.3.8

## v0.3.8 핵심 변경

- 내용 최대 너비/높이는 **최대 박스**로 동작하며 원본 종횡비를 유지합니다.
- 실제 내용 사각형을 작업 공간 안에서 좌/중/우 · 상/중/하 정렬합니다.
- PNG/SVG는 raw SVG viewBox가 아니라 **실제 벡터 경계**를 측정해 정렬합니다.
- 일반 크기 SWF는 스타일 적용 SVG를 native DefineShape3로 변환하는 경로를 다시 사용해, SVG에서 검증된 폐영역 채우기를 SWF에도 반영합니다.
- 100MB 이상 또는 모델 공간 50만 엔티티 이상은 기존 저메모리 CAD 직접 SWF 경로를 유지합니다. 대용량 최적화는 이번 버전의 주 대상이 아닙니다.


DWG 모델 공간을 SVG / PNG / 편집 가능한 벡터 SWF로 변환하는 Windows .NET 8 WinForms 도구입니다.

## v0.3.8 핵심 변경 — DWG → SWF 직접 경로

SWF 출력은 더 이상 전체 중간 SVG를 만들지 않습니다.

```text
기존 v0.3.2
DWG → ACadSharp.Image → 전체 SVG → 직접 SWF Writer → SWF

v0.3.8
DWG → ACadSharp Entity → 직접 SWF Shape Writer → SWF
```

- SWF는 `DefineShape3 + PlaceObject2` 기반의 자체 Writer를 그대로 사용합니다.
- `LineStyle` / `FillStyle`을 사용하므로 Adobe Animate에서 벡터 Shape로 가져오기 쉬운 구조를 유지합니다.
- Insert는 ACadSharp의 `Explode()`가 제공되면 월드 좌표로 전개해 처리합니다.
- LINE, Polyline, Arc/Circle/Ellipse/Spline, Text/MText, Hatch, Solid/Face, Insert 등 일반적인 2D 엔티티를 직접 처리합니다.
- 지원하지 못한 엔티티는 전체 변환을 중단하지 않고 건너뛰며 SWF 로그에 건너뛴 수를 표시합니다.
- 닫힌 도형 채우기 옵션도 SWF 직접 경로에 적용합니다.
  - 단일 폐곡선은 직접 채웁니다.
  - 연결 선분 폐영역은 NetTopologySuite polygonize를 사용합니다.
  - 연결 선분이 지나치게 많은 도면은 메모리 보호를 위해 해당 추가 채우기만 생략할 수 있습니다.

## 대용량 도면 동작

SWF만 선택한 경우 ACadSharp.Image의 전체 SVG DOM을 만들지 않습니다. 따라서 v0.2.9.x에서 121MB / 62만 엔티티 도면이 `[SVG 엔티티 렌더링/기록]` 단계에서 10GB 이상 메모리를 사용하다 실패하던 경로를 우회합니다.

SVG 또는 PNG도 함께 선택한 경우 처리 순서는 다음과 같습니다.

1. DWG 읽기
2. **SWF 직접 생성 및 저장**
3. PNG/SVG용 전체 SVG 생성
4. 스타일 처리
5. SVG 저장
6. PNG 렌더링

따라서 이후 PNG/SVG 단계가 메모리 부족으로 실패해도 SWF 직접 생성이 완료됐다면 SWF 파일은 이미 출력 폴더에 남습니다.

v0.3.8부터는 DWG를 읽은 직후 렌더링 전에 복잡도 사전 점검을 합니다. 50MB/30만 엔티티부터 주의, 100MB/50만 엔티티부터 고위험으로 안내하며, 고위험 도면은 기본적으로 취소가 선택됩니다. SWF 직접 경로도 복잡한 도면에서 깨진 결과가 나올 수 있으므로 정상 품질을 보장하지 않습니다.

## SVG / PNG

SVG와 PNG는 ACadSharp.Image 기반 경로를 유지합니다.

- 투명/지정 배경
- 선 색 일괄 지정
- 선 두께 일괄 지정
- 닫힌 영역 채우기
- 작업 공간 너비/높이
- 내용 최대 너비/높이(종횡비 유지)
- 좌/중/우 · 상/중/하 정렬
- 음수 SVG viewBox 원점 정규화
- raw SVG 캐시
- 대형 SVG 저장 단계 heartbeat 로그

매우 복잡한 DWG에서는 이 경로가 여전히 많은 메모리를 사용할 수 있습니다.

## SWF 보기

SWF 보기 기능은 사용자가 직접 배치한 구형 Chromium + `pepflashplayer.dll`을 사용합니다. Flash/Chromium 바이너리는 이 프로젝트에 포함하지 않습니다.

```text
Tools\FlashViewer\Chromium\chrome.exe
Tools\FlashViewer\PepperFlash\pepflashplayer.dll
Tools\FlashViewer\PepperFlash\manifest.json   # 선택
```

SWF 생성 자체에는 Chromium/Flash가 필요하지 않습니다.

## 빌드

Windows에서 .NET 8 SDK 설치 후:

```bat
publish-win-x64.bat
```

을 실행합니다.


## v0.3.8

- 여백 설정 제거. 출력 너비/높이는 작업 공간(Canvas) 크기입니다.
- 내용 최대 너비/높이를 별도로 지정하고 실제 도면 bounding rectangle을 종횡비 유지로 맞춘 뒤 작업 공간 안에서 좌/중/우, 상/중/하 정렬합니다.
- 직접 SWF 폐영역 재구성에 닫힌 outline도 참여시키고 polygon winding을 정규화했습니다.
- 대형/중첩 블록 처리 중에도 5초마다 방문 엔티티, 현재 처리 대상, Shape/Edge, 메모리 상태를 표시합니다.

## v0.3.8 변경

- SWF 닫힌 영역 fill winding 회귀를 되돌리고, 교차점 polygonize 결과를 직접 FillStyle0에 기록합니다.
- SWF는 DWG 헤더 EXTMIN/EXTMAX가 아니라 **실제로 생성된 벡터 점의 tight bounds**를 마지막에 측정합니다.
- `내용 너비/높이`는 이제 최대값이 아니라 **정확한 목표 사각형 크기**입니다. 내용은 그 크기에 맞춰 X/Y 각각 스케일되고 작업 공간에서 정렬됩니다.
- 30만 엔티티 이상 대형 도면은 패턴 Hatch의 내부선 `ExplodePattern()`을 자동 생략하고 경계만 처리해 장시간 정체를 줄입니다.

## v0.3.8 SWF 안정화
- M/L/H/V/Z 직선 SVG path는 곡선 샘플링을 하지 않고 원래 꼭짓점만 SWF edge로 기록합니다.
- 폐영역 채우기 polygon이 불필요하게 수백~수천 edge로 증가하던 문제를 수정했습니다.
- 새 Shape를 만들 때 20,000 edge 목표치를 넘기기 전에 분리하도록 개선했습니다.


## 복잡도 사전 점검 (v0.3.8)

DWG를 읽은 직후, 실제 렌더링을 시작하기 전에 파일 크기와 모델 공간 엔티티 수를 기준으로 위험도를 판정합니다.

- 주의: 50MB 이상 또는 모델 공간 엔티티 300,000개 이상
- 고위험: 100MB 이상 또는 모델 공간 엔티티 500,000개 이상

Hatch / Insert / Spline 개수도 함께 표시합니다. 이 수치는 현재 변환 엔진의 관찰된 한계를 기준으로 한 보수적 휴리스틱이며 DWG 일반 규격의 한계를 의미하지 않습니다.

고위험 도면에서는 선택한 출력으로 계속, SWF만 시도, 파일 건너뛰기를 선택할 수 있습니다. SWF만 시도해도 정상 품질을 보장하지 않습니다.
