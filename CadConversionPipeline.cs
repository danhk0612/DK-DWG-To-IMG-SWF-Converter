using ACadSharp.Image;
using ACadSharp.IO;
using SkiaSharp;
using Svg.Skia;
using System.Diagnostics;
using System.Reflection;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CSMath;

namespace DwgToPngPoC;

internal sealed class CadConversionPipeline
{
    private readonly Action<string> _log;
    private readonly Func<DrawingRiskAssessment, DrawingRiskDecision>? _riskDecision;

    public CadConversionPipeline(
        Action<string> log,
        Func<DrawingRiskAssessment, DrawingRiskDecision>? riskDecision = null)
    {
        _log = log;
        _riskDecision = riskDecision;
    }

    public ConversionResult Convert(string inputPath, string selectedOutput, ConverterSettings settings)
    {
        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        _log("  엔진: v0.3.8");

        var workingDirectory = Path.Combine(Path.GetTempPath(), "DwgConverter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var rawSvg = Path.Combine(workingDirectory, "raw.svg");
        var styledSvg = Path.Combine(workingDirectory, "styled.svg");
        var effectiveSettings = settings.Copy();
        var initialSvgPipelineRequested = effectiveSettings.ExportSvg || effectiveSettings.ExportPng;
        var rawSvgCacheHit = RawSvgCache.TryRestore(inputPath, effectiveSettings, rawSvg);
        var inputInfo = new FileInfo(inputPath);

        string? svgPath = null;
        string? pngPath = null;
        string? swfPath = null;
        ACadSharp.CadDocument? document = null;

        try
        {
            // SWF needs the DWG once so we can decide whether to use the normal, correctness-
            // focused SVG->native SWF route or retain the low-memory CAD-direct fallback for
            // exceptionally large drawings. PNG/SVG cache hits can still skip DWG parsing
            // when SWF is not requested.
            if (effectiveSettings.ExportSwf || (initialSvgPipelineRequested && !rawSvgCacheHit))
            {
                _log($"  [1/6] DWG 읽는 중... ({inputInfo.Length / 1024d / 1024d:0.0} MB)");
                stage.Restart();
                document = DwgReader.Read(inputPath);
                _log($"  [1/6] DWG 읽기 완료 ({stage.Elapsed.TotalSeconds:0.0}초)");

                var assessment = DrawingRiskAssessment.Analyze(inputInfo, document, effectiveSettings);
                _log($"  [사전 점검] {assessment.ToLogText()}");

                if (assessment.Level != DrawingRiskLevel.Normal && _riskDecision is not null)
                {
                    var decision = _riskDecision(assessment);
                    if (decision == DrawingRiskDecision.SkipFile)
                    {
                        _log("  [사전 점검] 사용자가 이 파일의 변환을 취소했습니다.");
                        return new ConversionResult(svgPath, pngPath, swfPath);
                    }

                    if (decision == DrawingRiskDecision.TrySwfOnly)
                    {
                        effectiveSettings.ExportSvg = false;
                        effectiveSettings.ExportPng = false;
                        effectiveSettings.ExportSwf = true;
                        _log("  [사전 점검] SWF만 시도합니다. 고복잡도 도면은 SWF도 결과 품질을 보장할 수 없습니다.");
                    }
                    else
                    {
                        _log("  [사전 점검] 선택한 출력 형식으로 계속합니다.");
                    }
                }
            }
            else
            {
                _log($"  [1/6] 원본 SVG 캐시 사용 - DWG 읽기 생략 ({GetFileSizeText(rawSvg)})");
            }

            var requestedSvgPipeline = effectiveSettings.ExportSvg || effectiveSettings.ExportPng;
            var entityCount = document?.ModelSpace.Entities.Count ?? 0;
            var useDirectCadSwf = effectiveSettings.ExportSwf && document is not null &&
                                  (inputInfo.Length >= 100L * 1024L * 1024L || entityCount >= 500_000);
            var needsSvgPipeline = requestedSvgPipeline || (effectiveSettings.ExportSwf && !useDirectCadSwf);

            if (requestedSvgPipeline && entityCount >= 500_000)
            {
                _log($"  [사전 점검] 모델 공간 엔티티 {entityCount:N0}개 - PNG/SVG용 전체 SVG 렌더링은 메모리 위험 구간입니다.");
            }

            if (effectiveSettings.ExportSwf)
            {
                if (useDirectCadSwf)
                {
                    if (document is null)
                        throw new InvalidOperationException("SWF 직접 변환에 필요한 DWG 문서가 로드되지 않았습니다.");

                    stage.Restart();
                    _log("  [2/6] 대용량 SWF 직접 변환 중... (중간 SVG 생성 없음)");
                    swfPath = BuildOutputPath(inputPath, selectedOutput, "SWF", ".swf");
                    var swfResult = CadDirectSwfExporter.Export(document, swfPath, effectiveSettings, _log);
                    _log($"  [2/6] SWF 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(swfPath)}, " +
                         $"Shape {swfResult.ShapeCount:N0}개 / Edge {swfResult.EdgeCount:N0}개 / " +
                         $"건너뜀 {swfResult.EntitiesSkipped:N0}개)");
                    _log($"  [2/6] SWF 저장 완료: {swfPath}");
                }
                else
                {
                    _log("  [2/6] SWF: 채우기/편집성 우선 경로 사용 - 스타일 적용 SVG 생성 후 native Shape로 변환 예정");
                }
            }
            else
            {
                _log("  [2/6] SWF 출력 건너뜀");
            }

            if (!needsSvgPipeline)
            {
                _log("  [3/6] PNG/SVG용 모델 공간 SVG 생성 건너뜀");
                _log("  [4/6] SVG 스타일 처리 건너뜀");
                _log("  [5/6] SVG 출력 건너뜀");
                _log("  [6/6] PNG 출력 건너뜀");
                _log($"  파일 처리 완료 (총 {total.Elapsed.TotalSeconds:0.0}초)");
                return new ConversionResult(svgPath, pngPath, swfPath);
            }

            if (rawSvgCacheHit)
            {
                _log($"  [3/6] 원본 SVG 생성 생략 (캐시 적중, {GetFileSizeText(rawSvg)})");
            }
            else
            {
                if (document is null)
                    throw new InvalidOperationException("PNG/SVG 변환에 필요한 DWG 문서가 로드되지 않았습니다.");

                stage.Restart();
                _log("  [3/6] PNG/SVG/SWF용 모델 공간을 SVG로 변환 중...");
                ExportRawSvg(document, rawSvg, effectiveSettings);
                _log($"  [3/6] 원본 SVG 생성 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(rawSvg)})");
                RawSvgCache.Store(inputPath, effectiveSettings, rawSvg);
            }

            stage.Restart();
            _log("  [4/6] 색상/선두께/채우기/내용크기/정렬 적용 중...");
            var styleResult = SvgStyleProcessor.Process(rawSvg, styledSvg, effectiveSettings);
            var fillDetail = effectiveSettings.FillClosedShapes
                ? $", 직접 폐곡선 {styleResult.DirectClosedShapeFills}개 + 교차점 포함 연결 폐영역 {styleResult.ReconstructedRegionFills}개 채움"
                : string.Empty;
            _log($"  [4/6] 스타일 적용 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(styledSvg)}{fillDetail})");

            // Normal-size SWF uses the already styled SVG. This is the v0.3.2 path that
            // preserved reconstructed region fills and produced Animate-friendly DefineShape3
            // output, while still avoiding the old svg2swf.exe dependency.
            if (effectiveSettings.ExportSwf && !useDirectCadSwf)
            {
                stage.Restart();
                _log("  [2/6] SWF native Shape 생성 중... (스타일 적용 SVG 기반)");
                swfPath = BuildOutputPath(inputPath, selectedOutput, "SWF", ".swf");
                var swfResult = SvgDirectSwfExporter.Export(styledSvg, swfPath, _log);
                _log($"  [2/6] SWF 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(swfPath)}, " +
                     $"Shape {swfResult.ShapeCount:N0}개 / Edge {swfResult.EdgeCount:N0}개 / 건너뜀 {swfResult.SkippedElements:N0}개)");
            }

            if (effectiveSettings.ExportSvg)
            {
                stage.Restart();
                _log("  [5/6] SVG 저장 중...");
                svgPath = BuildOutputPath(inputPath, selectedOutput, "SVG", ".svg");
                SaveStyledSvg(styledSvg, svgPath);
                _log($"  [5/6] SVG 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(svgPath)})");
            }
            else
            {
                _log("  [5/6] SVG 출력 건너뜀");
            }

            if (effectiveSettings.ExportPng)
            {
                stage.Restart();
                _log("  [6/6] PNG 렌더링 중...");
                pngPath = BuildOutputPath(inputPath, selectedOutput, "PNG", ".png");
                RasterizeSvg(styledSvg, pngPath);
                _log($"  [6/6] PNG 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {GetFileSizeText(pngPath)})");
            }
            else
            {
                _log("  [6/6] PNG 출력 건너뜀");
            }

            _log($"  파일 처리 완료 (총 {total.Elapsed.TotalSeconds:0.0}초)");
            return new ConversionResult(svgPath, pngPath, swfPath);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch
            {
                // Temporary files are safe to leave behind if Windows still has a handle open.
            }
        }
    }

    private static string GetFileSizeText(string path)
    {
        if (!File.Exists(path))
            return "파일 없음";

        var bytes = new FileInfo(path).Length;
        if (bytes >= 1024L * 1024L)
            return $"{bytes / 1024d / 1024d:0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private void ExportRawSvg(ACadSharp.CadDocument document, string outputPath, ConverterSettings settings)
    {
        var exporter = new ImageExporter();
        exporter.Configuration.Width = settings.Width;
        exporter.Configuration.Height = settings.Height;
        exporter.Configuration.SetPadding(0);
        exporter.Configuration.BackgroundColor = SixLabors.ImageSharp.Color.Transparent;
        exporter.Configuration.LayerVisibility = LayerVisibilityMode.Screen;
        exporter.Configuration.Svg.NonScalingStroke = false;
        exporter.Configuration.Svg.EmitSize = false;
        exporter.Configuration.Svg.EmitEntityAttributes = false;

        var stage = Stopwatch.StartNew();
        _log("    [3a] 모델 공간 프레임 구성 중...");

        if (TryAddModelSpaceWithoutBoundsScan(exporter, document, out var frameSource, out var frameText))
        {
            _log($"    [3a] 프레임 구성 완료 ({stage.Elapsed.TotalSeconds:0.0}초, {frameSource}: {frameText})");
        }
        else
        {
            _log("    [3a] DWG 헤더 경계가 유효하지 않아 기존 자동 Fit 방식 사용");
            exporter.AddModelSpace(document);
            _log($"    [3a] 자동 Fit 완료 ({stage.Elapsed.TotalSeconds:0.0}초)");
        }

        stage.Restart();
        _log("    [3b] SVG 엔티티 렌더링/기록 중...");
        SaveSvgWithHeartbeat(exporter, outputPath, stage);
        _log($"    [3b] SVG 렌더링/기록 완료 ({stage.Elapsed.TotalSeconds:0.0}초)");
    }

    private void SaveSvgWithHeartbeat(ImageExporter exporter, string outputPath, Stopwatch stage)
    {
        using var process = Process.GetCurrentProcess();
        var sync = new object();
        var lastCpu = process.TotalProcessorTime;
        var lastWall = stage.Elapsed;
        var finished = false;
        var memoryRiskLogged = false;

        using var timer = new System.Threading.Timer(_ =>
        {
            lock (sync)
            {
                if (finished)
                    return;

                try
                {
                    process.Refresh();
                    var nowWall = stage.Elapsed;
                    var nowCpu = process.TotalProcessorTime;
                    var wallDelta = Math.Max(0.001, (nowWall - lastWall).TotalSeconds);
                    var cpuDelta = Math.Max(0, (nowCpu - lastCpu).TotalSeconds);
                    var normalizedCpu = Math.Min(999.0,
                        cpuDelta / wallDelta / Math.Max(1, Environment.ProcessorCount) * 100.0);

                    var memoryMb = process.WorkingSet64 / 1024d / 1024d;
                    var svgSize = File.Exists(outputPath)
                        ? GetFileSizeText(outputPath)
                        : "아직 파일 생성 전";

                    _log($"    [3b] 작업 중... 경과 {nowWall.TotalSeconds:0}초, CPU {normalizedCpu:0.0}%, 메모리 {memoryMb:0} MB, SVG {svgSize}");

                    if (!memoryRiskLogged && memoryMb >= 10_240)
                    {
                        _log("    [3b] 경고: 프로세스 메모리가 10GB를 넘었습니다. 이 도면은 메모리 부족으로 실패할 가능성이 높습니다.");
                        memoryRiskLogged = true;
                    }

                    lastCpu = nowCpu;
                    lastWall = nowWall;
                }
                catch
                {
                    // Diagnostic heartbeat must never interrupt conversion.
                }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        try
        {
            exporter.Save(outputPath, ImageExportFormat.Svg);
        }
        finally
        {
            lock (sync)
                finished = true;
        }
    }

    private bool TryAddModelSpaceWithoutBoundsScan(
        ImageExporter exporter,
        ACadSharp.CadDocument document,
        out string frameSource,
        out string frameText)
    {
        var min = document.Header.ModelSpaceExtMin;
        var max = document.Header.ModelSpaceExtMax;
        frameSource = "EXTMIN/EXTMAX";

        if (!IsUsableFrame(min.X, min.Y, max.X, max.Y))
        {
            var limitsMin = document.Header.ModelSpaceLimitsMin;
            var limitsMax = document.Header.ModelSpaceLimitsMax;
            if (!IsUsableFrame(limitsMin.X, limitsMin.Y, limitsMax.X, limitsMax.Y))
            {
                frameText = "유효한 경계 없음";
                return false;
            }

            min = new CSMath.XYZ(limitsMin.X, limitsMin.Y, 0);
            max = new CSMath.XYZ(limitsMax.X, limitsMax.Y, 0);
            frameSource = "LIMMIN/LIMMAX";
        }

        var width = max.X - min.X;
        var height = max.Y - min.Y;
        frameText = $"X {min.X:0.###}~{max.X:0.###}, Y {min.Y:0.###}~{max.Y:0.###}";

        var layout = new Layout(Layout.ModelLayoutName)
        {
            PaperWidth = width,
            PaperHeight = height
        };

        var page = new ImagePage
        {
            Name = "Model",
            Document = document,
            Layout = layout,
            Translation = new XY(-min.X, -min.Y)
        };

        // Do NOT call ImagePage.Add(BlockRecord, ...), even with resizeLayout:false.
        // ACadSharp.Image 0.1.6 internally calls BlockRecord.GetSortedEntities() first,
        // which is extremely expensive on drawings with hundreds of thousands of entities.
        // Iterate the native entity collection directly and append entities one by one.
        var entityCount = document.ModelSpace.Entities.Count;
        _log($"    [3a] 모델 공간 엔티티 {entityCount:N0}개 직접 등록 시작 (GetSortedEntities 완전 우회)");

        var addStage = Stopwatch.StartNew();
        var added = 0;
        const int progressInterval = 50_000;

        foreach (var entity in document.ModelSpace.Entities)
        {
            if (entity is Viewport)
                continue;

            page.AddEntity(entity);
            added++;

            if (added % progressInterval == 0)
            {
                var rate = addStage.Elapsed.TotalSeconds > 0
                    ? added / addStage.Elapsed.TotalSeconds
                    : 0;
                _log($"    [3a] 엔티티 등록 {added:N0}/{entityCount:N0} ({rate:N0}개/초)");
            }
        }

        var finalRate = addStage.Elapsed.TotalSeconds > 0
            ? added / addStage.Elapsed.TotalSeconds
            : 0;
        _log($"    [3a] 엔티티 직접 등록 완료 ({added:N0}개, {addStage.Elapsed.TotalSeconds:0.0}초, {finalRate:N0}개/초)");

        var pagesField = typeof(ImageExporter).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ImageExporter).FullName, "_pages");

        if (pagesField.GetValue(exporter) is not List<ImagePage> pages)
        {
            throw new InvalidOperationException("ACadSharp.Image 내부 페이지 목록에 접근하지 못했습니다.");
        }

        pages.Add(page);
        return true;
    }

    private static bool IsUsableFrame(double minX, double minY, double maxX, double maxY)
    {
        if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY))
        {
            return false;
        }

        var width = maxX - minX;
        var height = maxY - minY;
        return width > 1e-9 && height > 1e-9;
    }

    private static void SaveStyledSvg(string styledSvgPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(styledSvgPath, outputPath, overwrite: true);
    }

    private static void RasterizeSvg(string svgPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var svg = new SKSvg();
        if (svg.Load(svgPath) is null || svg.Picture is null)
            throw new InvalidOperationException("SVG를 PNG로 렌더링하지 못했습니다.");

        var bounds = svg.Picture.CullRect;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));

        var imageInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo)
            ?? throw new InvalidOperationException("PNG 렌더링 표면을 생성하지 못했습니다.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(svg.Picture);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG 인코딩에 실패했습니다.");
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(output);
    }

    private static string BuildOutputPath(string inputPath, string selectedOutput, string defaultSubfolder, string extension)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(selectedOutput)
            ? Path.Combine(Path.GetDirectoryName(inputPath)!, defaultSubfolder)
            : selectedOutput;

        Directory.CreateDirectory(outputDirectory);
        return Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + extension);
    }
}

internal readonly record struct ConversionResult(string? SvgPath, string? PngPath, string? SwfPath);
