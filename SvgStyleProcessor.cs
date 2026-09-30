using System.Globalization;
using System.Xml.Linq;

namespace DwgToPngPoC;

internal static class SvgStyleProcessor
{
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    public static SvgProcessResult Process(string sourcePath, string outputPath, ConverterSettings settings)
    {
        var document = XDocument.Load(sourcePath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("SVG root element not found.");

        var sourceViewBox = ParseViewBox((string?)root.Attribute("viewBox"));

        RemoveExistingBackground(root);

        // Measure actual vector geometry instead of trusting the raw SVG viewBox. CAD header
        // extents can contain large empty areas or construction outliers, which made PNG/SVG
        // alignment differ from the direct SWF result.
        var artworkBounds = SvgDirectSwfExporter.TryMeasureArtworkBounds(root, out var measured)
            ? new ViewBox(measured.X, measured.Y, measured.Width, measured.Height)
            : sourceViewBox;
        var layout = CalculateLayout(artworkBounds, settings);

        ApplyLineOverrides(root, settings, layout.StrokeScale);
        var directClosedShapeFills = ApplyClosedShapeFill(root, settings);
        var reconstructedRegionFills = settings.FillClosedShapes
            ? ClosedRegionReconstructor.AddFills(
                root,
                NormalizeColor(settings.FillColor),
                layout.StrokeScale,
                sourceViewBox.Width,
                sourceViewBox.Height)
            : 0;
        RemoveVectorEffects(root);

        // ContentWidth/ContentHeight are a maximum content box. Keep the source aspect ratio
        // and align the actual fitted artwork inside the workspace.
        WrapContent(root, artworkBounds, layout);
        root.SetAttributeValue("width", settings.Width.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", settings.Height.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("viewBox", FormattableString.Invariant($"0 0 {settings.Width} {settings.Height}"));
        root.SetAttributeValue("preserveAspectRatio", "none");

        if (!settings.TransparentBackground)
        {
            var background = new XElement(
                SvgNs + "rect",
                new XAttribute("class", "app-background"),
                new XAttribute("x", "0"),
                new XAttribute("y", "0"),
                new XAttribute("width", settings.Width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("height", settings.Height.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("fill", NormalizeColor(settings.BackgroundColor)),
                new XAttribute("stroke", "none"));

            var defs = root.Elements().FirstOrDefault(e => e.Name.LocalName == "defs");
            if (defs is null)
                root.AddFirst(background);
            else
                defs.AddAfterSelf(background);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        document.Save(outputPath, SaveOptions.DisableFormatting);
        return new SvgProcessResult(directClosedShapeFills, reconstructedRegionFills);
    }

    private static LayoutResult CalculateLayout(ViewBox source, ConverterSettings settings)
    {
        var maxContentWidth = Math.Max(1, settings.ContentWidth);
        var maxContentHeight = Math.Max(1, settings.ContentHeight);
        var sourceWidth = Math.Max(source.Width, 1e-9);
        var sourceHeight = Math.Max(source.Height, 1e-9);

        var fitScaleX = maxContentWidth / sourceWidth;
        var fitScaleY = maxContentHeight / sourceHeight;

        var (scaleX, scaleY) = settings.FitMode switch
        {
            ContentFitMode.Fill => (Math.Max(fitScaleX, fitScaleY), Math.Max(fitScaleX, fitScaleY)),
            ContentFitMode.Stretch => (fitScaleX, fitScaleY),
            _ => (Math.Min(fitScaleX, fitScaleY), Math.Min(fitScaleX, fitScaleY))
        };

        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            throw new InvalidOperationException("도면 내용 배율을 계산할 수 없습니다.");

        var actualWidth = sourceWidth * scaleX;
        var actualHeight = sourceHeight * scaleY;
        var freeX = settings.Width - actualWidth;
        var freeY = settings.Height - actualHeight;
        var offsetX = settings.HorizontalPlacement switch
        {
            HorizontalPlacement.Left => 0.0,
            HorizontalPlacement.Right => freeX,
            _ => freeX / 2.0
        };
        var offsetY = settings.VerticalPlacement switch
        {
            VerticalPlacement.Top => 0.0,
            VerticalPlacement.Bottom => freeY,
            _ => freeY / 2.0
        };

        var strokeScale = Math.Sqrt(scaleX * scaleY);
        return new LayoutResult(offsetX, offsetY, scaleX, scaleY, strokeScale);
    }

    private static void WrapContent(XElement root, ViewBox source, LayoutResult layout)
    {
        var movable = root.Elements()
            .Where(e => e.Name.LocalName != "defs" &&
                        !string.Equals((string?)e.Attribute("class"), "app-background", StringComparison.Ordinal))
            .ToArray();

        var transform = FormattableString.Invariant(
            $"translate({layout.OffsetX:0.########} {layout.OffsetY:0.########}) scale({layout.ScaleX:0.########} {layout.ScaleY:0.########}) translate({-source.X:0.########} {-source.Y:0.########})");

        var group = new XElement(SvgNs + "g",
            new XAttribute("class", "app-content"),
            new XAttribute("transform", transform));
        foreach (var element in movable)
        {
            element.Remove();
            group.Add(element);
        }
        root.Add(group);
    }

    private static void ApplyLineOverrides(XElement root, ConverterSettings settings, double pixelScale)
    {
        if (!settings.OverrideStrokeColor && !settings.OverrideStrokeWidth)
            return;

        var color = NormalizeColor(settings.StrokeColor);
        var widthInUserUnits = (double)settings.StrokeWidthPixels / pixelScale;
        var widthText = widthInUserUnits.ToString("0.########", CultureInfo.InvariantCulture);

        foreach (var element in root.DescendantsAndSelf())
        {
            if (!CanCarryStroke(element))
                continue;

            if (!IsStroked(element))
                continue;

            if (settings.OverrideStrokeColor)
                element.SetAttributeValue("stroke", color);

            if (settings.OverrideStrokeWidth)
                element.SetAttributeValue("stroke-width", widthText);
        }

        // Layer groups carry inherited CAD stroke properties. Override those too so shapes
        // that do not have an explicit stroke attribute receive the same setting.
        foreach (var layer in root.Descendants(SvgNs + "g")
                     .Where(g => string.Equals((string?)g.Attribute("class"), "cad-layer", StringComparison.Ordinal)))
        {
            if (settings.OverrideStrokeColor)
                layer.SetAttributeValue("stroke", color);
            if (settings.OverrideStrokeWidth)
                layer.SetAttributeValue("stroke-width", widthText);
        }
    }

    private static bool IsStroked(XElement element)
    {
        var ownStroke = (string?)element.Attribute("stroke");
        if (string.Equals(ownStroke, "none", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(ownStroke))
            return true;

        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            var inherited = (string?)parent.Attribute("stroke");
            if (string.Equals(inherited, "none", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(inherited))
                return true;
        }

        return false;
    }

    private static bool CanCarryStroke(XElement element)
    {
        return element.Name.LocalName is "line" or "polyline" or "polygon" or "path" or "circle" or "ellipse" or "rect";
    }

    private static int ApplyClosedShapeFill(XElement root, ConverterSettings settings)
    {
        if (!settings.FillClosedShapes)
            return 0;

        var applied = 0;
        var fillColor = NormalizeColor(settings.FillColor);
        foreach (var element in root.Descendants())
        {
            if (!IsEligibleClosedShape(element))
                continue;

            // Existing CAD fills (hatches, solids, text, point markers, wipeouts) already
            // carry an explicit fill. This option only fills previously unfilled closed outlines.
            var existingFill = (string?)element.Attribute("fill");
            if (!string.IsNullOrWhiteSpace(existingFill) &&
                !string.Equals(existingFill, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            element.SetAttributeValue("fill", fillColor);
            applied++;
        }

        return applied;
    }

    private static bool IsEligibleClosedShape(XElement element)
    {
        var name = element.Name.LocalName;
        if (name is "circle" or "ellipse" or "polygon" or "rect")
            return IsOutlineGeometry(element);

        if (name == "polyline" && IsOutlineGeometry(element))
            return IsClosedPolyline(element);

        if (name != "path" || !IsOutlineGeometry(element))
            return false;

        var path = ((string?)element.Attribute("d"))?.Trim();
        return !string.IsNullOrEmpty(path) &&
               (path.EndsWith('Z') || path.EndsWith('z'));
    }

    private static bool IsClosedPolyline(XElement element)
    {
        var points = ((string?)element.Attribute("points"))?
            .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN)
            .ToArray();

        if (points is null || points.Length < 6 || points.Length % 2 != 0 || points.Any(double.IsNaN))
            return false;

        const double tolerance = 1e-9;
        return Math.Abs(points[0] - points[^2]) <= tolerance &&
               Math.Abs(points[1] - points[^1]) <= tolerance;
    }

    private static bool IsOutlineGeometry(XElement element)
    {
        var stroke = (string?)element.Attribute("stroke");
        if (string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase))
            return false;

        // When stroke is inherited from a layer group the element has no own stroke.
        return IsStroked(element);
    }

    private static void RemoveVectorEffects(XElement root)
    {
        foreach (var attribute in root.DescendantsAndSelf().Attributes("vector-effect").ToArray())
            attribute.Remove();
    }

    private static void RemoveExistingBackground(XElement root)
    {
        foreach (var element in root.Descendants()
                     .Where(e => e.Name.LocalName == "rect" &&
                                 string.Equals((string?)e.Attribute("class"), "cad-background", StringComparison.Ordinal))
                     .ToArray())
        {
            element.Remove();
        }
    }

    private static ViewBox ParseViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("SVG viewBox is missing.");

        var parts = value
            .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();

        if (parts.Length != 4 || parts[2] <= 0 || parts[3] <= 0)
            throw new InvalidDataException("SVG viewBox is invalid.");

        return new ViewBox(parts[0], parts[1], parts[2], parts[3]);
    }

    private static string NormalizeColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "#000000";

        value = value.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        if (value.Length != 7)
            throw new InvalidOperationException($"잘못된 색상 값: {value}");

        return value.ToUpperInvariant();
    }

    public readonly record struct SvgProcessResult(int DirectClosedShapeFills, int ReconstructedRegionFills);

    private readonly record struct ViewBox(double X, double Y, double Width, double Height);
    private readonly record struct LayoutResult(
        double OffsetX,
        double OffsetY,
        double ScaleX,
        double ScaleY,
        double StrokeScale);
}
