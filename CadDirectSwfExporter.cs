using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;

namespace DwgToPngPoC;

/// <summary>
/// Low-memory DWG -> SWF vector path.
///
/// Unlike the PNG/SVG route, this exporter never asks ACadSharp.Image to build one
/// complete SVG DOM for the drawing. CAD entities are flattened directly into the
/// minimal DefineShape3 writer. This is primarily intended for very large drawings
/// where the full SVG renderer can consume many gigabytes of memory.
/// </summary>
internal static class CadDirectSwfExporter
{
    private const int MaxExpansionDepth = 16;
    private const int ProgressInterval = 25_000;
    private const int MaxRegionSegments = 1_500_000;

    public static CadDirectSwfExportResult Export(
        CadDocument document,
        string outputPath,
        ConverterSettings settings,
        Action<string> log)
    {
        var layout = CreateLayout(document, settings);
        var background = settings.TransparentBackground
            ? new SwfRgba(255, 255, 255, 255)
            : ParseHex(settings.BackgroundColor, new SwfRgba(255, 255, 255, 255));

        var swf = new DirectSwfDocument(settings.Width, settings.Height, background);
        var regions = settings.FillClosedShapes ? new RegionCollector() : null;
        var stats = new MutableStats();
        var watch = Stopwatch.StartNew();
        var modelCount = document.ModelSpace.Entities.Count;
        stats.SimplifyPatternHatches = modelCount >= 300_000;

        log($"    [SWF-DIRECT] 모델 공간 {modelCount:N0}개 엔티티 직접 변환 시작");
        log($"    [SWF-DIRECT] 전체 SVG DOM 생성 없음 / 작업 공간 {settings.Width:N0}×{settings.Height:N0}px / " +
            $"내용 최대 공간 {settings.ContentWidth:N0}×{settings.ContentHeight:N0}px (비율 유지) / " +
            $"초기 좌표 배율 {layout.Scale:0.######}");
        if (stats.SimplifyPatternHatches)
            log("    [SWF-DIRECT] 대용량 최적화: 패턴 Hatch 내부선 전개를 생략하고 경계만 처리합니다.");

        var index = 0;
        using var process = Process.GetCurrentProcess();
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            try
            {
                process.Refresh();
                log($"    [SWF-DIRECT] 작업 중... 경과 {watch.Elapsed.TotalSeconds:0}초, " +
                    $"루트 {index:N0}/{modelCount:N0}, 방문 {stats.Visited:N0}, 현재 {stats.CurrentOperation}, " +
                    $"Shape {swf.ShapeCount:N0}, Edge {swf.EdgeCount:N0}, 메모리 {process.WorkingSet64 / 1024d / 1024d:0} MB");
            }
            catch
            {
                // Diagnostic heartbeat must never interrupt conversion.
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        foreach (var entity in document.ModelSpace.Entities)
        {
            index++;
            stats.RootIndex = index;
            RenderEntity(entity, swf, regions, layout, settings, stats, 0);

            if (index % ProgressInterval == 0)
            {
                var rate = index / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                log($"    [SWF-DIRECT] 루트 {index:N0}/{modelCount:N0} 처리, {rate:N0}개/초, " +
                    $"방문 {stats.Visited:N0}, Shape {swf.ShapeCount:N0}, Edge {swf.EdgeCount:N0}, 건너뜀 {stats.Skipped:N0}");
            }
        }

        var reconstructed = 0;
        if (regions is not null)
        {
            if (regions.Count > MaxRegionSegments)
            {
                log($"    [SWF-DIRECT] 연결 폐영역 채우기 생략: 선분 {regions.Count:N0}개가 안전 한도 {MaxRegionSegments:N0}개를 초과했습니다.");
            }
            else if (regions.Count >= 3)
            {
                log($"    [SWF-DIRECT] 연결 선분 {regions.Count:N0}개 교차점/폐영역 계산 중...");
                reconstructed = regions.EmitBackFills(swf, ParseHex(settings.FillColor, new SwfRgba(255, 255, 255, 255)));
                log($"    [SWF-DIRECT] 연결 폐영역 {reconstructed:N0}개 채움");
            }
        }

        var fit = swf.FitContentToWorkspace(
            settings.ContentWidth,
            settings.ContentHeight,
            settings.HorizontalPlacement,
            settings.VerticalPlacement);
        if (fit.HasContent)
        {
            log($"    [SWF-DIRECT] 실제 벡터 경계 {fit.SourceWidthPx:0.#}×{fit.SourceHeightPx:0.#}px → " +
                $"내용 공간 {fit.TargetWidthPx:0.#}×{fit.TargetHeightPx:0.#}px, 위치 ({fit.TargetXpx:0.#}, {fit.TargetYpx:0.#}), " +
                $"배율 X {fit.ScaleX:0.####} / Y {fit.ScaleY:0.####}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, swf.ToBytes(compress: true, version: 10));

        log($"    [SWF-DIRECT] 완료: 방문 {stats.Visited:N0}, 블록/분해 {stats.Expanded:N0}, " +
            $"선형 {stats.Linear:N0}, 곡선 {stats.Curves:N0}, 문자 {stats.Text:N0}, " +
            $"기존 면 {stats.NativeFills:N0}, 직접 폐곡선 면 {stats.ClosedFills:N0}, 건너뜀 {stats.Skipped:N0}");

        return new CadDirectSwfExportResult(
            swf.ShapeCount,
            swf.EdgeCount,
            stats.Visited,
            stats.Skipped,
            reconstructed);
    }

    private static void RenderEntity(
        Entity entity,
        DirectSwfDocument swf,
        RegionCollector? regions,
        LayoutTransform layout,
        ConverterSettings settings,
        MutableStats stats,
        int depth)
    {
        if (depth > MaxExpansionDepth)
        {
            stats.Skipped++;
            return;
        }

        stats.Visited++;
        var typeName = entity.GetType().Name;
        stats.CurrentOperation = depth == 0 ? typeName : $"{typeName} (블록/분해 depth {depth})";
        if (!IsVisible(entity))
            return;

        var stroke = ResolveEntityColor(entity, settings);
        var strokeWidth = ResolveStrokeWidth(entity, settings);

        try
        {
            switch (typeName)
            {
                case "Line":
                    if (TryGetPointProperty(entity, "StartPoint", out var lineStart) &&
                        TryGetPointProperty(entity, "EndPoint", out var lineEnd))
                    {
                        var pts = new[] { layout.Map(lineStart), layout.Map(lineEnd) };
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, closed: false);
                        stats.Linear++;
                        return;
                    }
                    break;

                case "Arc":
                    if (TryPolygonalVertices(entity, 72, out var arcPoints) && arcPoints.Count >= 2)
                    {
                        var pts = arcPoints.Select(layout.Map).ToArray();
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, closed: false);
                        stats.Curves++;
                        return;
                    }
                    break;

                case "Circle":
                    if (TryPolygonalVertices(entity, 96, out var circlePoints) && circlePoints.Count >= 3)
                    {
                        var pts = Close(circlePoints.Select(layout.Map));
                        if (settings.FillClosedShapes)
                        {
                            swf.AddBackFill(new[] { (IReadOnlyList<SwfPoint>)pts },
                                ParseHex(settings.FillColor, new SwfRgba(255, 255, 255, 255)));
                            stats.ClosedFills++;
                        }
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, closed: true);
                        stats.Curves++;
                        return;
                    }
                    break;

                case "Ellipse":
                    if (TryPolygonalVertices(entity, 112, out var ellipsePoints) && ellipsePoints.Count >= 2)
                    {
                        var closed = IsClosedCurve(entity, ellipsePoints);
                        var pts = ellipsePoints.Select(layout.Map).ToList();
                        if (closed)
                        {
                            pts = Close(pts).ToList();
                            if (settings.FillClosedShapes)
                            {
                                swf.AddBackFill(new[] { (IReadOnlyList<SwfPoint>)pts },
                                    ParseHex(settings.FillColor, new SwfRgba(255, 255, 255, 255)));
                                stats.ClosedFills++;
                            }
                        }
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, closed);
                        stats.Curves++;
                        return;
                    }
                    break;

                case "Spline":
                    if (TryPolygonalVertices(entity, 128, out var splinePoints) && splinePoints.Count >= 2)
                    {
                        var closed = GetBool(entity, "IsClosed");
                        var pts = splinePoints.Select(layout.Map).ToList();
                        if (closed)
                        {
                            pts = Close(pts).ToList();
                            if (settings.FillClosedShapes)
                            {
                                swf.AddBackFill(new[] { (IReadOnlyList<SwfPoint>)pts },
                                    ParseHex(settings.FillColor, new SwfRgba(255, 255, 255, 255)));
                                stats.ClosedFills++;
                            }
                        }
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, closed);
                        stats.Curves++;
                        return;
                    }
                    break;

                case "LwPolyline":
                case "Polyline2D":
                case "Polyline3D":
                    if (TryFlattenPolyline(entity, layout.Scale, out var cadPolyline, out var polylineClosed) && cadPolyline.Count >= 2)
                    {
                        var pts = cadPolyline.Select(layout.Map).ToList();
                        if (polylineClosed)
                        {
                            pts = Close(pts).ToList();
                            if (settings.FillClosedShapes)
                            {
                                swf.AddBackFill(new[] { (IReadOnlyList<SwfPoint>)pts },
                                    ParseHex(settings.FillColor, new SwfRgba(255, 255, 255, 255)));
                                stats.ClosedFills++;
                            }
                        }
                        swf.AddPolyline(pts, strokeWidth, stroke);
                        regions?.AddPolyline(pts, polylineClosed);
                        stats.Linear++;
                        return;
                    }
                    break;

                case "Solid":
                case "Face3D":
                    if (TryGetCorners(entity, out var corners))
                    {
                        var pts = Close(corners.Select(layout.Map));
                        swf.AddFill(new[] { (IReadOnlyList<SwfPoint>)pts }, stroke);
                        stats.NativeFills++;
                        return;
                    }
                    break;

                case "TextEntity":
                case "AttributeEntity":
                case "AttributeDefinition":
                    if (TryRenderText(entity, false, swf, layout, stroke))
                    {
                        stats.Text++;
                        return;
                    }
                    break;

                case "MText":
                    if (TryRenderText(entity, true, swf, layout, stroke))
                    {
                        stats.Text++;
                        return;
                    }
                    break;

                case "Hatch":
                    if (TryRenderHatch(entity, swf, regions, layout, settings, stats, depth))
                        return;
                    break;

                case "Insert":
                    if (TryExplodeAndRender(entity, swf, regions, layout, settings, stats, depth))
                    {
                        RenderInsertAttributes(entity, swf, regions, layout, settings, stats, depth);
                        return;
                    }
                    break;

                case "Point":
                    if (TryGetPointProperty(entity, "Location", out var point))
                    {
                        var p = layout.Map(point);
                        const double size = 1.5;
                        swf.AddPolyline(new[] { new SwfPoint(p.X - size, p.Y), new SwfPoint(p.X + size, p.Y) }, strokeWidth, stroke);
                        swf.AddPolyline(new[] { new SwfPoint(p.X, p.Y - size), new SwfPoint(p.X, p.Y + size) }, strokeWidth, stroke);
                        stats.Linear++;
                        return;
                    }
                    break;
            }

            if (typeName.StartsWith("Dimension", StringComparison.Ordinal) &&
                TryRenderBlockPicture(entity, swf, regions, layout, settings, stats, depth))
            {
                return;
            }

            // Last chance for entities that expose an Explode() API in ACadSharp.
            if (TryExplodeAndRender(entity, swf, regions, layout, settings, stats, depth))
                return;
        }
        catch
        {
            // One malformed/unsupported entity must not abort a whole large drawing.
        }

        stats.Skipped++;
    }

    private static bool TryRenderHatch(
        Entity entity,
        DirectSwfDocument swf,
        RegionCollector? regions,
        LayoutTransform layout,
        ConverterSettings settings,
        MutableStats stats,
        int depth)
    {
        var isSolid = GetBool(entity, "IsSolid");
        var color = ResolveEntityColor(entity, settings, allowStrokeOverride: false);

        if (TryGetHatchRings(entity, layout, out var rings) && rings.Count > 0)
        {
            if (isSolid)
            {
                swf.AddFill(rings, color);
                stats.NativeFills += rings.Count;
                return true;
            }

            if (stats.SimplifyPatternHatches)
            {
                var stroke = ResolveEntityColor(entity, settings);
                var width = ResolveStrokeWidth(entity, settings);
                foreach (var ring in rings)
                {
                    swf.AddPolyline(ring, width, stroke);
                    regions?.AddPolyline(ring, closed: true);
                }
                stats.Linear += rings.Count;
                return true;
            }
        }

        // For normal drawings preserve pattern hatch detail. On very large drawings
        // this path is intentionally skipped above because ExplodePattern can spend
        // tens of seconds on a single hatch and create huge temporary collections.
        var method = entity.GetType().GetMethod("ExplodePattern", Type.EmptyTypes);
        if (method?.Invoke(entity, null) is IEnumerable exploded)
        {
            var any = false;
            foreach (var item in exploded)
            {
                if (item is not Entity child)
                    continue;
                RenderEntity(child, swf, null, layout, settings, stats, depth + 1);
                any = true;
            }
            if (any)
            {
                stats.Expanded++;
                return true;
            }
        }

        return stats.SimplifyPatternHatches;
    }

    private static bool TryGetHatchRings(Entity hatch, LayoutTransform layout, out List<IReadOnlyList<SwfPoint>> rings)
    {
        rings = [];
        if (GetProperty(hatch, "Paths") is not IEnumerable paths)
            return false;

        foreach (var path in paths)
        {
            if (path is null || GetProperty(path, "Edges") is not IEnumerable edges)
                continue;

            var ring = new List<CadPoint>();
            foreach (var edge in edges)
            {
                if (edge is null)
                    continue;

                var toEntity = edge.GetType().GetMethod("ToEntity", Type.EmptyTypes);
                if (toEntity?.Invoke(edge, null) is not Entity edgeEntity)
                    continue;

                if (!TryEntityAsCadPolyline(edgeEntity, out var edgePoints) || edgePoints.Count < 2)
                    continue;

                if (ring.Count == 0)
                {
                    ring.AddRange(edgePoints);
                }
                else
                {
                    var first = edgePoints[0];
                    var last = edgePoints[^1];
                    var current = ring[^1];
                    if (DistanceSquared(current, last) < DistanceSquared(current, first))
                        edgePoints.Reverse();
                    if (DistanceSquared(ring[^1], edgePoints[0]) < 1e-18)
                        ring.AddRange(edgePoints.Skip(1));
                    else
                        ring.AddRange(edgePoints);
                }
            }

            if (ring.Count >= 3)
                rings.Add(Close(ring.Select(layout.Map)));
        }

        return rings.Count > 0;
    }

    private static bool TryEntityAsCadPolyline(Entity entity, out List<CadPoint> points)
    {
        points = [];
        var name = entity.GetType().Name;
        if (name == "Line" &&
            TryGetPointProperty(entity, "StartPoint", out var a) &&
            TryGetPointProperty(entity, "EndPoint", out var b))
        {
            points = [a, b];
            return true;
        }

        if (name is "Arc" or "Circle" or "Ellipse" or "Spline")
            return TryPolygonalVertices(entity, name == "Spline" ? 128 : 72, out points);

        if (name is "LwPolyline" or "Polyline2D" or "Polyline3D")
            return TryFlattenPolyline(entity, 1.0, out points, out _);

        return false;
    }

    private static bool TryExplodeAndRender(
        Entity entity,
        DirectSwfDocument swf,
        RegionCollector? regions,
        LayoutTransform layout,
        ConverterSettings settings,
        MutableStats stats,
        int depth)
    {
        var method = entity.GetType().GetMethod("Explode", Type.EmptyTypes);
        if (method is null)
            return false;

        if (method.Invoke(entity, null) is not IEnumerable exploded)
            return false;

        var any = false;
        foreach (var item in exploded)
        {
            if (item is not Entity child || ReferenceEquals(child, entity))
                continue;
            RenderEntity(child, swf, regions, layout, settings, stats, depth + 1);
            any = true;
        }

        if (any)
            stats.Expanded++;
        return any;
    }

    private static void RenderInsertAttributes(
        Entity insert,
        DirectSwfDocument swf,
        RegionCollector? regions,
        LayoutTransform layout,
        ConverterSettings settings,
        MutableStats stats,
        int depth)
    {
        if (GetProperty(insert, "Attributes") is not IEnumerable attributes)
            return;
        foreach (var item in attributes)
            if (item is Entity attribute)
                RenderEntity(attribute, swf, regions, layout, settings, stats, depth + 1);
    }

    private static bool TryRenderBlockPicture(
        Entity entity,
        DirectSwfDocument swf,
        RegionCollector? regions,
        LayoutTransform layout,
        ConverterSettings settings,
        MutableStats stats,
        int depth)
    {
        var block = GetProperty(entity, "Block");
        if (block is null || GetProperty(block, "Entities") is not IEnumerable entities)
            return false;

        var any = false;
        foreach (var item in entities)
        {
            if (item is not Entity child)
                continue;
            RenderEntity(child, swf, regions, layout, settings, stats, depth + 1);
            any = true;
        }
        if (any)
            stats.Expanded++;
        return any;
    }

    private static bool TryRenderText(Entity entity, bool isMText, DirectSwfDocument swf, LayoutTransform layout, SwfRgba color)
    {
        var text = isMText
            ? Convert.ToString(GetProperty(entity, "PlainText"), CultureInfo.InvariantCulture)
            : Convert.ToString(GetProperty(entity, "Value"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!TryGetPointProperty(entity, "InsertPoint", out var insert))
            return false;

        var height = GetDouble(entity, "Height", 1.0);
        if (!double.IsFinite(height) || height <= 0)
            height = 1.0;
        var rotation = GetDouble(entity, "Rotation", 0.0);
        var widthFactor = isMText ? 1.0 : Math.Clamp(GetDouble(entity, "WidthFactor", 1.0), 0.01, 100.0);
        var fontSizePx = Math.Clamp(height * layout.Scale, 0.5, 4096.0);
        var anchor = layout.Map(insert);

        var familyName = "Malgun Gothic";
        var style = GetProperty(entity, "Style");
        var fontFile = Convert.ToString(GetProperty(style, "File"), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(fontFile) && fontFile.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            familyName = Path.GetFileNameWithoutExtension(fontFile);

        try
        {
            using var family = TryFontFamily(familyName) ?? new FontFamily("Arial");
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoClip;

            var lines = isMText
                ? text.Split(new[] { "\r\n", "\r", "\n", "\\P" }, StringSplitOptions.None)
                : new[] { text };

            var lineOffset = 0.0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    lineOffset += fontSizePx * 1.2;
                    continue;
                }

                using var path = new GraphicsPath();
                path.AddString(line, family, (int)FontStyle.Regular, (float)fontSizePx,
                    new PointF(0, 0), format);

                using var matrix = new Matrix();
                matrix.Scale((float)widthFactor, 1f, MatrixOrder.Append);
                matrix.Rotate((float)(-rotation * 180.0 / Math.PI), MatrixOrder.Append);
                matrix.Translate((float)anchor.X, (float)(anchor.Y + lineOffset - fontSizePx), MatrixOrder.Append);
                path.Transform(matrix);
                path.Flatten(null, Math.Max(0.15f, (float)(fontSizePx / 50.0)));

                var contours = GraphicsPathToContours(path);
                if (contours.Count > 0)
                    swf.AddFill(contours, color);
                lineOffset += fontSizePx * 1.2;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FontFamily? TryFontFamily(string name)
    {
        try { return new FontFamily(name); }
        catch { return null; }
    }

    private static List<IReadOnlyList<SwfPoint>> GraphicsPathToContours(GraphicsPath path)
    {
        var result = new List<IReadOnlyList<SwfPoint>>();
        var points = path.PathPoints;
        var types = path.PathTypes;
        List<SwfPoint>? current = null;

        for (var i = 0; i < points.Length; i++)
        {
            var type = types[i] & 0x07;
            if (type == 0 || current is null)
            {
                if (current is { Count: >= 3 })
                    result.Add(Close(current));
                current = [];
            }

            current.Add(new SwfPoint(points[i].X, points[i].Y));
            if ((types[i] & 0x80) != 0 && current.Count >= 3)
            {
                result.Add(Close(current));
                current = null;
            }
        }

        if (current is { Count: >= 3 })
            result.Add(Close(current));
        return result;
    }

    private static bool TryPolygonalVertices(Entity entity, int precision, out List<CadPoint> points)
    {
        points = [];
        var method = entity.GetType().GetMethod("PolygonalVertexes", new[] { typeof(int) });
        if (method is null)
            return false;

        if (method.Invoke(entity, new object[] { precision }) is not IEnumerable values)
            return false;

        foreach (var value in values)
            if (TryPoint(value, out var p))
                points.Add(p);
        return points.Count > 0;
    }

    private static bool TryFlattenPolyline(Entity entity, double pixelScale, out List<CadPoint> points, out bool closed)
    {
        points = [];
        closed = GetBool(entity, "IsClosed");
        if (GetProperty(entity, "Vertices") is not IEnumerable vertices)
            return false;

        var source = new List<(CadPoint Point, double Bulge)>();
        foreach (var vertex in vertices)
        {
            if (vertex is null)
                continue;
            var location = GetProperty(vertex, "Location") ?? vertex;
            if (!TryPoint(location, out var point))
                continue;
            source.Add((point, GetDouble(vertex, "Bulge", 0)));
        }

        if (source.Count < 2)
            return false;

        var segmentCount = closed ? source.Count : source.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var a = source[i];
            var b = source[(i + 1) % source.Count];
            if (i == 0)
                points.Add(a.Point);

            if (Math.Abs(a.Bulge) < 1e-10)
            {
                points.Add(b.Point);
                continue;
            }

            AppendBulge(points, a.Point, b.Point, a.Bulge, pixelScale);
        }

        return points.Count >= 2;
    }

    private static void AppendBulge(List<CadPoint> target, CadPoint a, CadPoint b, double bulge, double pixelScale)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord <= 1e-12)
        {
            target.Add(b);
            return;
        }

        var sweep = 4.0 * Math.Atan(bulge);
        var mx = (a.X + b.X) / 2.0;
        var my = (a.Y + b.Y) / 2.0;
        var offsetFactor = (1.0 - bulge * bulge) / (4.0 * bulge);
        var cx = mx - dy * offsetFactor;
        var cy = my + dx * offsetFactor;
        var radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));
        var start = Math.Atan2(a.Y - cy, a.X - cx);
        var approxPixels = Math.Abs(sweep) * radius * Math.Max(pixelScale, 1e-9);
        var segments = Math.Clamp((int)Math.Ceiling(approxPixels / 2.0), 4, 128);

        for (var i = 1; i <= segments; i++)
        {
            var angle = start + sweep * i / segments;
            target.Add(new CadPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
        }
    }

    private static bool TryGetCorners(Entity entity, out List<CadPoint> points)
    {
        points = [];
        foreach (var name in new[] { "FirstCorner", "SecondCorner", "FourthCorner", "ThirdCorner" })
            if (TryGetPointProperty(entity, name, out var p))
                points.Add(p);
        return points.Count >= 3;
    }

    private static bool IsClosedCurve(Entity entity, List<CadPoint> points)
    {
        if (GetBool(entity, "IsClosed"))
            return true;

        var start = GetDouble(entity, "StartParameter", double.NaN);
        var end = GetDouble(entity, "EndParameter", double.NaN);
        if (double.IsFinite(start) && double.IsFinite(end))
        {
            var span = Math.Abs(end - start);
            if (span >= Math.PI * 2.0 - 1e-6)
                return true;
        }

        return points.Count >= 3 && DistanceSquared(points[0], points[^1]) < 1e-16;
    }

    private static LayoutTransform CreateLayout(CadDocument document, ConverterSettings settings)
    {
        var min = document.Header.ModelSpaceExtMin;
        var max = document.Header.ModelSpaceExtMax;
        if (!IsUsableFrame(min.X, min.Y, max.X, max.Y))
        {
            var limMin = document.Header.ModelSpaceLimitsMin;
            var limMax = document.Header.ModelSpaceLimitsMax;
            if (!IsUsableFrame(limMin.X, limMin.Y, limMax.X, limMax.Y))
                throw new InvalidOperationException("SWF 직접 변환에 사용할 유효한 EXTMIN/EXTMAX 또는 LIMMIN/LIMMAX가 없습니다.");
            min = new XYZ(limMin.X, limMin.Y, 0);
            max = new XYZ(limMax.X, limMax.Y, 0);
        }

        // Header extents are used only as a safe provisional normalization so SWF coordinates
        // stay in a manageable range while entities are collected. The final placement does
        // NOT use these extents: DirectSwfDocument.FitContentToWorkspace() measures the actual
        // generated vector points and fits those to the exact content rectangle.
        var sourceWidth = max.X - min.X;
        var sourceHeight = max.Y - min.Y;
        var scale = Math.Min(
            Math.Max(1, settings.Width) / sourceWidth,
            Math.Max(1, settings.Height) / sourceHeight);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("SWF 직접 변환의 초기 좌표 배율을 계산할 수 없습니다.");

        return new LayoutTransform(min.X, max.Y, scale, 0, 0);
    }

    private static bool IsUsableFrame(double minX, double minY, double maxX, double maxY) =>
        double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(maxX) && double.IsFinite(maxY) &&
        maxX - minX > 1e-9 && maxY - minY > 1e-9;

    private static bool IsVisible(Entity entity)
    {
        if (entity.IsInvisible)
            return false;

        var layer = entity.Layer;
        if (layer is null)
            return true;

        return !GetBool(layer, "IsFrozen") && !GetBool(layer, "IsOff");
    }

    private static SwfRgba ResolveEntityColor(Entity entity, ConverterSettings settings, bool allowStrokeOverride = true)
    {
        if (allowStrokeOverride && settings.OverrideStrokeColor)
            return ParseHex(settings.StrokeColor, new SwfRgba(0, 0, 0, 255));

        try
        {
            var c = entity.GetActiveColor();
            var r = c.R;
            var g = c.G;
            var b = c.B;
            var bg = settings.TransparentBackground
                ? new SwfRgba(255, 255, 255, 255)
                : ParseHex(settings.BackgroundColor, new SwfRgba(255, 255, 255, 255));

            var bgIsDark = bg.R + bg.G + bg.B < 384;
            if (bgIsDark && r == 0 && g == 0 && b == 0)
                return new SwfRgba(255, 255, 255, 255);
            if (!bgIsDark && r == 255 && g == 255 && b == 255)
                return new SwfRgba(0, 0, 0, 255);
            return new SwfRgba(r, g, b, 255);
        }
        catch
        {
            return new SwfRgba(0, 0, 0, 255);
        }
    }

    private static double ResolveStrokeWidth(Entity entity, ConverterSettings settings)
    {
        if (settings.OverrideStrokeWidth)
            return Math.Max(0.05, (double)settings.StrokeWidthPixels);

        try
        {
            var raw = Convert.ToInt32(entity.GetActiveLineWeightType(), CultureInfo.InvariantCulture);
            return raw > 0 ? Math.Clamp(raw / 25.0, 0.5, 6.0) : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static SwfRgba ParseHex(string? text, SwfRgba fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        var value = text.Trim();
        if (value.StartsWith('#'))
            value = value[1..];
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return fallback;
        return new SwfRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    private static object? GetProperty(object? source, string name)
    {
        if (source is null)
            return null;
        try { return source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source); }
        catch { return null; }
    }

    private static bool GetBool(object? source, string name)
    {
        var value = GetProperty(source, name);
        try { return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static double GetDouble(object? source, string name, double fallback = 0)
    {
        var value = GetProperty(source, name);
        try { return value is null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static bool TryGetPointProperty(object source, string property, out CadPoint point) =>
        TryPoint(GetProperty(source, property), out point);

    private static bool TryPoint(object? source, out CadPoint point)
    {
        point = default;
        if (source is null)
            return false;
        var x = GetDouble(source, "X", double.NaN);
        var y = GetDouble(source, "Y", double.NaN);
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return false;
        point = new CadPoint(x, y);
        return true;
    }

    private static IReadOnlyList<SwfPoint> Close(IEnumerable<SwfPoint> source)
    {
        var points = source.ToList();
        if (points.Count > 0 && !Near(points[0], points[^1]))
            points.Add(points[0]);
        return points;
    }

    private static double DistanceSquared(CadPoint a, CadPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static bool Near(SwfPoint a, SwfPoint b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    private readonly record struct CadPoint(double X, double Y);

    private readonly record struct LayoutTransform(
        double MinX,
        double MaxY,
        double Scale,
        double OffsetX,
        double OffsetY)
    {
        public SwfPoint Map(CadPoint p) =>
            new((p.X - MinX) * Scale + OffsetX, (MaxY - p.Y) * Scale + OffsetY);
    }

    private sealed class MutableStats
    {
        public int RootIndex;
        public int Visited;
        public int Expanded;
        public int Linear;
        public int Curves;
        public int Text;
        public int NativeFills;
        public int ClosedFills;
        public int Skipped;
        public bool SimplifyPatternHatches;
        public string CurrentOperation = "준비";
    }

    private sealed class RegionCollector
    {
        private readonly List<(SwfPoint A, SwfPoint B)> _segments = [];
        public int Count => _segments.Count;

        public void AddPolyline(IReadOnlyList<SwfPoint> points, bool closed)
        {
            if (points.Count < 2 || _segments.Count > MaxRegionSegments)
                return;

            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                if (!Near(a, b))
                    _segments.Add((a, b));
            }

            if (closed && !Near(points[0], points[^1]))
            {
                var a = points[^1];
                var b = points[0];
                if (!Near(a, b))
                    _segments.Add((a, b));
            }
        }

        public int EmitBackFills(DirectSwfDocument swf, SwfRgba color)
        {
            if (_segments.Count < 3)
                return 0;

            var factory = new GeometryFactory(new PrecisionModel(1_000_000));
            var lines = new List<Geometry>(_segments.Count);
            foreach (var (a, b) in _segments)
            {
                lines.Add(factory.CreateLineString([
                    new Coordinate(a.X, a.Y),
                    new Coordinate(b.X, b.Y)
                ]));
            }

            Geometry noded;
            try
            {
                noded = UnaryUnionOp.Union(lines, factory);
            }
            catch
            {
                return 0;
            }

            var polygonizer = new Polygonizer(false);
            polygonizer.Add(noded);
            var count = 0;
            foreach (var polygon in polygonizer.GetPolygons().OfType<Polygon>())
            {
                if (polygon.IsEmpty || polygon.Area <= 4.0)
                    continue;

                // Keep the natural polygonizer winding. The reference SWF writer uses
                // FillStyle0 directly and Animate handles these native rings correctly.
                // Reversing every ring here caused valid fills to disappear in v0.3.4.
                var rings = new List<IReadOnlyList<SwfPoint>>
                {
                    ToRing(polygon.ExteriorRing)
                };
                for (var i = 0; i < polygon.NumInteriorRings; i++)
                    rings.Add(ToRing(polygon.GetInteriorRingN(i)));

                swf.AddBackFill(rings, color);
                count++;
            }
            return count;
        }

        private static IReadOnlyList<SwfPoint> ToRing(LineString ring)
        {
            return ring.Coordinates.Select(c => new SwfPoint(c.X, c.Y)).ToArray();
        }

    }
}

internal readonly record struct CadDirectSwfExportResult(
    int ShapeCount,
    int EdgeCount,
    int EntitiesVisited,
    int EntitiesSkipped,
    int ReconstructedRegions);
