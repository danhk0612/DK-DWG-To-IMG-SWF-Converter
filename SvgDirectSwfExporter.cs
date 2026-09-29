using SkiaSharp;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace DwgToPngPoC;

internal static class SvgDirectSwfExporter
{
    private static readonly Regex LinearPathTokenRegex = new(
        @"[MmLlHhVvZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryMeasureArtworkBounds(XElement root, out SvgArtworkBounds bounds)
    {
        var probe = new DirectSwfDocument(1, 1, new SwfRgba(255, 255, 255, 255));
        var stats = new MutableStats();
        Traverse(root, StyleState.Default, Matrix3x2.Identity, probe, stats);
        if (probe.TryGetContentBoundsPx(out var measured))
        {
            bounds = new SvgArtworkBounds(measured.X, measured.Y, measured.Width, measured.Height);
            return measured.Width > 0 && measured.Height > 0;
        }

        bounds = default;
        return false;
    }

    public static SwfExportResult Export(string svgPath, string outputPath, Action<string>? log = null)
    {
        var xdoc = XDocument.Load(svgPath, LoadOptions.PreserveWhitespace);
        var root = xdoc.Root ?? throw new InvalidDataException("SVG root element not found.");
        var viewBox = ParseViewBox((string?)root.Attribute("viewBox"));
        var width = ParseLength((string?)root.Attribute("width"), viewBox.Width);
        var height = ParseLength((string?)root.Attribute("height"), viewBox.Height);
        var widthPx = Math.Max(1, (int)Math.Round(width));
        var heightPx = Math.Max(1, (int)Math.Round(height));

        var sx = widthPx / viewBox.Width;
        var sy = heightPx / viewBox.Height;
        var stageMatrix = Matrix3x2.CreateTranslation((float)-viewBox.X, (float)-viewBox.Y) *
                          Matrix3x2.CreateScale((float)sx, (float)sy);

        var background = FindBackground(root) ?? new SwfRgba(255, 255, 255, 255);
        var swf = new DirectSwfDocument(widthPx, heightPx, background);
        var stats = new MutableStats();

        Traverse(root, StyleState.Default, stageMatrix, swf, stats);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, swf.ToBytes());
        log?.Invoke($"    [SWF] 직접 Shape 기록: {swf.ShapeCount:N0} shapes, {swf.EdgeCount:N0} edges, " +
                    $"line {stats.Lines:N0}, path {stats.Paths:N0} (직선 {stats.LinearPaths:N0} / 곡선 {stats.CurvedPaths:N0}), " +
                    $"circle/ellipse {stats.RoundShapes:N0}, polygon {stats.Polygons:N0}, " +
                    $"text outline {stats.Texts:N0}, skipped {stats.Skipped:N0}");

        return new SwfExportResult(swf.ShapeCount, swf.EdgeCount, stats.Skipped);
    }

    private static void Traverse(
        XElement element,
        StyleState inherited,
        Matrix3x2 inheritedTransform,
        DirectSwfDocument swf,
        MutableStats stats)
    {
        var style = inherited.With(element);
        var transform = ParseTransform((string?)element.Attribute("transform")) * inheritedTransform;
        var name = element.Name.LocalName;

        if (name == "rect" && string.Equals((string?)element.Attribute("class"), "app-background", StringComparison.Ordinal))
            return;

        switch (name)
        {
            case "line":
                AddLine(element, style, transform, swf, stats);
                break;
            case "polyline":
            case "polygon":
                AddPoly(element, style, transform, swf, stats, name == "polygon");
                break;
            case "rect":
                AddRect(element, style, transform, swf, stats);
                break;
            case "circle":
            case "ellipse":
                AddEllipse(element, style, transform, swf, stats, name == "circle");
                break;
            case "path":
                AddPath(element, style, transform, swf, stats);
                break;
            case "text":
                AddText(element, style, transform, swf, stats);
                break;
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName is "defs" or "style" or "metadata")
                continue;
            Traverse(child, style, transform, swf, stats);
        }
    }

    private static void AddLine(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats)
    {
        if (!style.HasStroke)
            return;
        var pts = new[]
        {
            Map(matrix, Attr(e, "x1"), Attr(e, "y1")),
            Map(matrix, Attr(e, "x2"), Attr(e, "y2"))
        };
        swf.AddPolyline(pts, StrokeWidthInPixels(style, matrix), style.Stroke!.Value);
        stats.Lines++;
    }

    private static void AddPoly(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats, bool forceClosed)
    {
        var values = ParseNumbers((string?)e.Attribute("points"));
        if (values.Count < 4)
            return;
        var pts = new List<SwfPoint>();
        for (var i = 0; i + 1 < values.Count; i += 2)
            pts.Add(Map(matrix, values[i], values[i + 1]));

        var closed = forceClosed || (pts.Count >= 3 && Near(pts[0], pts[^1]));
        if (closed && !Near(pts[0], pts[^1]))
            pts.Add(pts[0]);

        if (style.HasFill && closed)
            swf.AddFill([pts], style.Fill!.Value);
        if (style.HasStroke)
            swf.AddPolyline(pts, StrokeWidthInPixels(style, matrix), style.Stroke!.Value);
        stats.Polygons++;
    }

    private static void AddRect(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats)
    {
        var x = Attr(e, "x");
        var y = Attr(e, "y");
        var w = Attr(e, "width");
        var h = Attr(e, "height");
        if (w <= 0 || h <= 0)
            return;
        var pts = new List<SwfPoint>
        {
            Map(matrix, x, y), Map(matrix, x + w, y), Map(matrix, x + w, y + h), Map(matrix, x, y + h), Map(matrix, x, y)
        };
        if (style.HasFill)
            swf.AddFill([pts], style.Fill!.Value);
        if (style.HasStroke)
            swf.AddPolyline(pts, StrokeWidthInPixels(style, matrix), style.Stroke!.Value);
        stats.Polygons++;
    }

    private static void AddEllipse(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats, bool circle)
    {
        var cx = Attr(e, "cx");
        var cy = Attr(e, "cy");
        var rx = circle ? Attr(e, "r") : Attr(e, "rx");
        var ry = circle ? rx : Attr(e, "ry");
        if (rx <= 0 || ry <= 0)
            return;

        // 64 segments is enough for editor-friendly CAD preview/output and avoids giant SWFs.
        var pts = new List<SwfPoint>(65);
        for (var i = 0; i <= 64; i++)
        {
            var a = Math.PI * 2.0 * i / 64.0;
            pts.Add(Map(matrix, cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry));
        }
        if (style.HasFill)
            swf.AddFill([pts], style.Fill!.Value);
        if (style.HasStroke)
            swf.AddPolyline(pts, StrokeWidthInPixels(style, matrix), style.Stroke!.Value);
        stats.RoundShapes++;
    }

    private static void AddPath(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats)
    {
        var data = ((string?)e.Attribute("d"))?.Trim();
        if (string.IsNullOrWhiteSpace(data))
            return;

        List<PathContour> contours;

        // CAD and reconstructed region fills are commonly already straight M/L/H/V/Z paths.
        // Do NOT feed these through SKPathMeasure: sampling a 1000px straight wall every 1.5px
        // turns a 2-edge line into hundreds of SWF edges and can make the resulting SWF enormous
        // or practically impossible for Animate/Flash Player to parse.
        if (TryParseLinearPathContours(data, matrix, out contours))
        {
            stats.LinearPaths++;
        }
        else
        {
            using var path = SKPath.ParseSvgPathData(data);
            if (path is null || path.IsEmpty)
            {
                stats.Skipped++;
                return;
            }

            contours = FlattenPath(path, matrix);
            stats.CurvedPaths++;
        }

        if (contours.Count == 0)
            return;

        if (style.HasFill)
        {
            var rings = contours
                .Where(c => c.Closed && c.Points.Count >= 3)
                .Select(c => (IReadOnlyList<SwfPoint>)c.Points)
                .ToArray();
            if (rings.Length > 0)
                swf.AddFill(rings, style.Fill!.Value);
        }
        if (style.HasStroke)
        {
            var width = StrokeWidthInPixels(style, matrix);
            foreach (var contour in contours)
            {
                if (contour.Points.Count >= 2)
                    swf.AddPolyline(contour.Points, width, style.Stroke!.Value);
            }
        }
        stats.Paths++;
    }

    private static bool TryParseLinearPathContours(string data, Matrix3x2 matrix, out List<PathContour> contours)
    {
        var parsedContours = new List<PathContour>();
        contours = parsedContours;
        if (Regex.IsMatch(data, "[AaCcQqSsTt]", RegexOptions.CultureInvariant))
            return false;

        var tokens = LinearPathTokenRegex.Matches(data).Select(m => m.Value).ToArray();
        if (tokens.Length == 0)
            return false;

        var i = 0;
        char command = '\0';
        double currentX = 0, currentY = 0;
        double startX = 0, startY = 0;
        var haveCurrent = false;
        List<SwfPoint>? current = null;

        void FinishOpenContour()
        {
            if (current is { Count: >= 2 })
                parsedContours.Add(new PathContour(RemoveConsecutiveDuplicates(current), false));
            current = null;
        }

        while (i < tokens.Length)
        {
            if (IsLinearPathCommand(tokens[i]))
            {
                command = tokens[i][0];
                i++;
                if (command is 'Z' or 'z')
                {
                    if (current is { Count: >= 2 })
                    {
                        var first = current[0];
                        if (!Near(first, current[^1]))
                            current.Add(first);
                        var cleaned = RemoveConsecutiveDuplicates(current);
                        if (cleaned.Count >= 3)
                            parsedContours.Add(new PathContour(cleaned, true));
                    }
                    current = null;
                    currentX = startX;
                    currentY = startY;
                    haveCurrent = true;
                    command = '\0';
                    continue;
                }
            }

            if (command == '\0')
                return false;

            switch (command)
            {
                case 'M':
                case 'm':
                {
                    if (!TryReadLinearPair(tokens, ref i, out var x, out var y))
                        return false;
                    FinishOpenContour();
                    if (command == 'm' && haveCurrent)
                    {
                        currentX += x;
                        currentY += y;
                    }
                    else
                    {
                        currentX = x;
                        currentY = y;
                    }
                    startX = currentX;
                    startY = currentY;
                    haveCurrent = true;
                    current = [Map(matrix, currentX, currentY)];
                    command = command == 'm' ? 'l' : 'L';
                    break;
                }

                case 'L':
                case 'l':
                {
                    if (!haveCurrent || !TryReadLinearPair(tokens, ref i, out var x, out var y))
                        return false;
                    if (command == 'l')
                    {
                        currentX += x;
                        currentY += y;
                    }
                    else
                    {
                        currentX = x;
                        currentY = y;
                    }
                    current ??= [Map(matrix, currentX, currentY)];
                    current.Add(Map(matrix, currentX, currentY));
                    break;
                }

                case 'H':
                case 'h':
                {
                    if (!haveCurrent || !TryReadLinearNumber(tokens, ref i, out var x))
                        return false;
                    currentX = command == 'h' ? currentX + x : x;
                    current ??= [Map(matrix, currentX, currentY)];
                    current.Add(Map(matrix, currentX, currentY));
                    break;
                }

                case 'V':
                case 'v':
                {
                    if (!haveCurrent || !TryReadLinearNumber(tokens, ref i, out var y))
                        return false;
                    currentY = command == 'v' ? currentY + y : y;
                    current ??= [Map(matrix, currentX, currentY)];
                    current.Add(Map(matrix, currentX, currentY));
                    break;
                }

                default:
                    return false;
            }
        }

        FinishOpenContour();
        contours = parsedContours;
        return parsedContours.Count > 0;
    }

    private static List<SwfPoint> RemoveConsecutiveDuplicates(List<SwfPoint> points)
    {
        if (points.Count <= 1)
            return points;
        var result = new List<SwfPoint>(points.Count) { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            if (!Near(result[^1], points[i]))
                result.Add(points[i]);
        }
        return result;
    }

    private static bool TryReadLinearPair(string[] tokens, ref int index, out double x, out double y)
    {
        x = y = 0;
        return TryReadLinearNumber(tokens, ref index, out x) &&
               TryReadLinearNumber(tokens, ref index, out y);
    }

    private static bool TryReadLinearNumber(string[] tokens, ref int index, out double value)
    {
        value = 0;
        if (index >= tokens.Length || IsLinearPathCommand(tokens[index]))
            return false;
        if (!double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        index++;
        return true;
    }

    private static bool IsLinearPathCommand(string token) =>
        token.Length == 1 && token[0] is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'Z' or 'z';

    private static void AddText(XElement e, StyleState style, Matrix3x2 matrix, DirectSwfDocument swf, MutableStats stats)
    {
        var value = e.Value;
        if (string.IsNullOrEmpty(value) || !style.HasFill)
            return;

        var x = Attr(e, "x");
        var y = Attr(e, "y");
        var fontSize = Attr(e, "font-size", 12);
        var familyName = ((string?)e.Attribute("font-family"))?.Split(',')[0].Trim(' ', '\'', '"');
        if (string.IsNullOrWhiteSpace(familyName))
            familyName = "Arial";

        try
        {
            using var family = new System.Drawing.FontFamily(familyName);
            using var path = new GraphicsPath();
            using var format = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone();
            path.AddString(value, family, (int)System.Drawing.FontStyle.Regular, (float)fontSize,
                new System.Drawing.PointF((float)x, (float)(y - fontSize)), format);
            path.Flatten(null, Math.Max(0.1f, (float)(fontSize / 40.0)));

            var rings = GraphicsPathToContours(path, matrix);
            if (rings.Count > 0)
                swf.AddFill(rings, style.Fill!.Value);
            stats.Texts++;
        }
        catch
        {
            stats.Skipped++;
        }
    }

    private static List<PathContour> FlattenPath(SKPath path, Matrix3x2 matrix)
    {
        var result = new List<PathContour>();
        using var measure = new SKPathMeasure(path, false, 1f);
        do
        {
            var length = measure.Length;
            if (length <= 0)
                continue;

            // Target roughly 1px sampling after transform, but cap points for pathological curves.
            var scale = MatrixScale(matrix);
            var lengthPx = length * scale;
            var segments = Math.Clamp((int)Math.Ceiling(lengthPx / 1.5), 1, 20_000);
            var points = new List<SwfPoint>(segments + 2);
            for (var i = 0; i <= segments; i++)
            {
                var distance = length * i / segments;
                if (measure.GetPosition(distance, out var pos))
                    points.Add(Map(matrix, pos.X, pos.Y));
            }
            if (measure.IsClosed && points.Count > 0 && !Near(points[0], points[^1]))
                points.Add(points[0]);
            result.Add(new PathContour(points, measure.IsClosed));
        } while (measure.NextContour());
        return result;
    }

    private static List<IReadOnlyList<SwfPoint>> GraphicsPathToContours(GraphicsPath path, Matrix3x2 matrix)
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
                    result.Add(current);
                current = [];
            }
            current.Add(Map(matrix, points[i].X, points[i].Y));
            var close = (types[i] & 0x80) != 0;
            if (close && current.Count >= 3)
            {
                if (!Near(current[0], current[^1]))
                    current.Add(current[0]);
                result.Add(current);
                current = null;
            }
        }
        if (current is { Count: >= 3 })
            result.Add(current);
        return result;
    }

    private static double StrokeWidthInPixels(StyleState style, Matrix3x2 matrix) =>
        Math.Max(0.05, style.StrokeWidth * MatrixScale(matrix));

    private static double MatrixScale(Matrix3x2 matrix)
    {
        var sx = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        var sy = Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22);
        return Math.Max(1e-9, (sx + sy) / 2.0);
    }

    private static Matrix3x2 ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Matrix3x2.Identity;

        var result = Matrix3x2.Identity;
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ',')) index++;
            var start = index;
            while (index < text.Length && char.IsLetter(text[index])) index++;
            if (index == start) break;
            var name = text[start..index].ToLowerInvariant();
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length || text[index] != '(') break;
            var close = text.IndexOf(')', ++index);
            if (close < 0) break;
            var args = ParseNumbers(text[index..close]);
            index = close + 1;

            Matrix3x2 m = Matrix3x2.Identity;
            if (name == "translate" && args.Count >= 1)
                m = Matrix3x2.CreateTranslation((float)args[0], (float)(args.Count > 1 ? args[1] : 0));
            else if (name == "scale" && args.Count >= 1)
                m = Matrix3x2.CreateScale((float)args[0], (float)(args.Count > 1 ? args[1] : args[0]));
            else if (name == "rotate" && args.Count >= 1)
            {
                var angle = (float)(args[0] * Math.PI / 180.0);
                if (args.Count >= 3)
                    m = Matrix3x2.CreateRotation(angle, new Vector2((float)args[1], (float)args[2]));
                else
                    m = Matrix3x2.CreateRotation(angle);
            }
            else if (name == "matrix" && args.Count >= 6)
                m = new Matrix3x2((float)args[0], (float)args[1], (float)args[2], (float)args[3], (float)args[4], (float)args[5]);

            // SVG transform lists use column-vector semantics, while System.Numerics.Matrix3x2
            // transforms row vectors. Pre-multiply each parsed operation so a list such as
            // translate(offset) scale(s) translate(-source) maps to
            // (point - source) * s + offset instead of scaling/translating in the reverse order.
            result = m * result;
        }
        return result;
    }

    private static SwfPoint Map(Matrix3x2 matrix, double x, double y)
    {
        var v = Vector2.Transform(new Vector2((float)x, (float)y), matrix);
        return new SwfPoint(v.X, v.Y);
    }

    private static SwfRgba? FindBackground(XElement root)
    {
        var bg = root.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "rect" &&
            string.Equals((string?)e.Attribute("class"), "app-background", StringComparison.Ordinal));
        return bg is null ? null : ParseColor((string?)bg.Attribute("fill"), 255);
    }

    private static ViewBox ParseViewBox(string? value)
    {
        var nums = ParseNumbers(value);
        if (nums.Count != 4 || nums[2] <= 0 || nums[3] <= 0)
            throw new InvalidDataException("SVG viewBox is invalid.");
        return new ViewBox(nums[0], nums[1], nums[2], nums[3]);
    }

    private static double ParseLength(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var clean = value.Trim();
        var i = 0;
        while (i < clean.Length && (char.IsDigit(clean[i]) || clean[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        return double.TryParse(clean[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static double Attr(XElement e, string name, double fallback = 0) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static List<double> ParseNumbers(string? text)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var span = text.AsSpan();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var c = i < span.Length ? span[i] : ' ';
            var numeric = char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E';
            if (numeric && start < 0) start = i;
            if (!numeric && start >= 0)
            {
                if (double.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    result.Add(v);
                start = -1;
            }
        }
        return result;
    }

    private static SwfRgba? ParseColor(string? value, byte alpha)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        var v = value.Trim();
        if (v.StartsWith('#') && v.Length == 7 &&
            byte.TryParse(v.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(v.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(v.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new SwfRgba(r, g, b, alpha);
        return new SwfRgba(0, 0, 0, alpha);
    }

    private static bool Near(SwfPoint a, SwfPoint b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    private readonly record struct ViewBox(double X, double Y, double Width, double Height);
    private sealed record PathContour(List<SwfPoint> Points, bool Closed);
    private sealed class MutableStats
    {
        public int Lines;
        public int Paths;
        public int LinearPaths;
        public int CurvedPaths;
        public int RoundShapes;
        public int Polygons;
        public int Texts;
        public int Skipped;
    }

    private readonly record struct StyleState(SwfRgba? Stroke, SwfRgba? Fill, double StrokeWidth)
    {
        public static StyleState Default => new(null, new SwfRgba(0, 0, 0, 255), 1);
        public bool HasStroke => Stroke is { A: > 0 };
        public bool HasFill => Fill is { A: > 0 };

        public StyleState With(XElement e)
        {
            var stroke = Stroke;
            var fill = Fill;
            var width = StrokeWidth;
            var opacity = ParseOpacity((string?)e.Attribute("opacity"), 1);
            var strokeOpacity = ParseOpacity((string?)e.Attribute("stroke-opacity"), 1) * opacity;
            var fillOpacity = ParseOpacity((string?)e.Attribute("fill-opacity"), 1) * opacity;

            var strokeText = (string?)e.Attribute("stroke");
            if (strokeText is not null)
                stroke = ParseColor(strokeText, (byte)Math.Round(255 * strokeOpacity));
            else if (stroke is not null && strokeOpacity < 0.999)
                stroke = stroke.Value with { A = (byte)Math.Round(stroke.Value.A * strokeOpacity) };

            var fillText = (string?)e.Attribute("fill");
            if (fillText is not null)
                fill = ParseColor(fillText, (byte)Math.Round(255 * fillOpacity));
            else if (fill is not null && fillOpacity < 0.999)
                fill = fill.Value with { A = (byte)Math.Round(fill.Value.A * fillOpacity) };

            if (double.TryParse((string?)e.Attribute("stroke-width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                width = Math.Max(0, w);
            return new StyleState(stroke, fill, width);
        }

        private static double ParseOpacity(string? value, double fallback) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : fallback;
    }
}

internal readonly record struct SvgArtworkBounds(double X, double Y, double Width, double Height);

internal readonly record struct SwfExportResult(int ShapeCount, int EdgeCount, int SkippedElements);
