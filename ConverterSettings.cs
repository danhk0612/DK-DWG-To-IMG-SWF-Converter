using System.Text.Json;

namespace DwgToPngPoC;

internal enum HorizontalPlacement
{
    Left,
    Center,
    Right
}

internal enum VerticalPlacement
{
    Top,
    Center,
    Bottom
}

internal enum ContentFitMode
{
    FitInside,
    Fill,
    Stretch
}

internal sealed class ConverterSettings
{
    public bool ExportSvg { get; set; }
    public bool ExportPng { get; set; } = true;
    public bool ExportSwf { get; set; }
    public int Width { get; set; } = 2400;
    public int Height { get; set; } = 1600;

    public bool TransparentBackground { get; set; } = true;
    public string BackgroundColor { get; set; } = "#FFFFFF";

    public bool OverrideStrokeColor { get; set; } = true;
    public string StrokeColor { get; set; } = "#000000";
    public bool OverrideStrokeWidth { get; set; } = true;
    public decimal StrokeWidthPixels { get; set; } = 1.0m;

    public bool FillClosedShapes { get; set; } = true;
    public string FillColor { get; set; } = "#FFFFFF";

    // Rectangle used to fit drawing content. Width/Height are the independent workspace/canvas size.
    public int ContentWidth { get; set; } = 2400;
    public int ContentHeight { get; set; } = 1600;
    public ContentFitMode FitMode { get; set; } = ContentFitMode.FitInside;
    public HorizontalPlacement HorizontalPlacement { get; set; } = HorizontalPlacement.Center;
    public VerticalPlacement VerticalPlacement { get; set; } = VerticalPlacement.Center;

    public string OutputFolder { get; set; } = string.Empty;

    public ConverterSettings Copy() => (ConverterSettings)MemberwiseClone();
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DwgConverter");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static ConverterSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new ConverterSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ConverterSettings>(json, JsonOptions) ?? new ConverterSettings();
        }
        catch
        {
            return new ConverterSettings();
        }
    }

    public static void Save(ConverterSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
