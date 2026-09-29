using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DwgToPngPoC;

internal static class RawSvgCache
{
    private const int FormatVersion = 3;

    private static string CacheDirectory => Path.Combine(SettingsStore.SettingsDirectory, "cache");

    public static bool TryRestore(string inputPath, ConverterSettings settings, string destinationPath)
    {
        try
        {
            var (svgPath, metadataPath) = GetPaths(inputPath);
            if (!File.Exists(svgPath) || !File.Exists(metadataPath))
                return false;

            var metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath));
            if (metadata is null || !Matches(metadata, inputPath, settings))
                return false;

            File.Copy(svgPath, destinationPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Store(string inputPath, ConverterSettings settings, string sourceSvgPath)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var (svgPath, metadataPath) = GetPaths(inputPath);
            File.Copy(sourceSvgPath, svgPath, overwrite: true);

            var info = new FileInfo(inputPath);
            var metadata = new CacheMetadata(
                FormatVersion,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                settings.Width,
                settings.Height);

            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        }
        catch
        {
            // Cache failure must never prevent the actual conversion.
        }
    }

    private static bool Matches(CacheMetadata metadata, string inputPath, ConverterSettings settings)
    {
        var info = new FileInfo(inputPath);
        return metadata.FormatVersion == FormatVersion &&
               metadata.FileLength == info.Length &&
               metadata.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
               metadata.Width == settings.Width &&
               metadata.Height == settings.Height;
    }

    private static (string SvgPath, string MetadataPath) GetPaths(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..32];
        return (
            Path.Combine(CacheDirectory, hash + ".svg"),
            Path.Combine(CacheDirectory, hash + ".json"));
    }

    private sealed record CacheMetadata(
        int FormatVersion,
        long FileLength,
        long LastWriteUtcTicks,
        int Width,
        int Height);
}
