using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DwgToPngPoC;

internal static class LegacyFlashViewer
{
    private const string ChromiumPackageUrl =
        "https://github.com/danhk0612/DK-DWG-To-IMG-SWF-Converter/releases/download/viewer-chromium-53.0.2785.0-x86/chromium-53.0.2785.0-x86.zip";
    private const string ChromiumPackageSha256 =
        "66ddd4f54b5bbb21ee87eba2beed9677f2edbe87592372ca74ed2d42f6c4bce2";

    private static readonly HttpClient HttpClient = new();

    public static string ToolDirectory => Path.Combine(AppContext.BaseDirectory, "Tools", "FlashViewer");
    public static string ChromiumDirectory => Path.Combine(ToolDirectory, "Chromium");
    public static string ChromiumExecutablePath => Path.Combine(ChromiumDirectory, "chrome.exe");
    public static string PepperDirectory => Path.Combine(ToolDirectory, "PepperFlash");
    public static string PepperFlashPath => Path.Combine(PepperDirectory, "pepflashplayer.dll");
    public static string PepperManifestPath => Path.Combine(PepperDirectory, "manifest.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ToolDirectory);
        Directory.CreateDirectory(PepperDirectory);
    }

    public static bool HasChromium => File.Exists(ChromiumExecutablePath);
    public static bool HasPepperFlash => File.Exists(PepperFlashPath);
    public static bool IsChromiumX86 => HasChromium && IsX86PortableExecutable(ChromiumExecutablePath);
    public static bool IsPepperFlashX86 => HasPepperFlash && IsX86PortableExecutable(PepperFlashPath);
    public static bool IsAvailable => IsChromiumX86 && IsPepperFlashX86;

    public static string StatusText
    {
        get
        {
            if (!HasChromium && !HasPepperFlash)
                return "Chromium / Pepper Flash 없음";
            if (!HasChromium)
                return "Chromium 없음 (다운로드 가능)";
            if (!IsChromiumX86)
                return "Chromium x86 필요";
            if (!HasPepperFlash)
                return "pepflashplayer.dll x86 없음";
            if (!IsPepperFlashX86)
                return "Pepper Flash x86 필요";
            return "사용 가능";
        }
    }

    public static async Task InstallChromiumAsync()
    {
        Directory.CreateDirectory(ToolDirectory);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"chromium-53.0.2785.0-x86-{Guid.NewGuid():N}.zip");
        var stagingDirectory = Path.Combine(
            ToolDirectory,
            $".Chromium-install-{Guid.NewGuid():N}");

        try
        {
            using (var response = await HttpClient.GetAsync(
                       ChromiumPackageUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(packagePath);
                await input.CopyToAsync(output);
            }

            await using (var stream = File.OpenRead(packagePath))
            using (var sha256 = SHA256.Create())
            {
                var actualHash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
                if (!string.Equals(actualHash, ChromiumPackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Chromium 다운로드 파일의 SHA-256이 일치하지 않습니다.{Environment.NewLine}" +
                        $"예상: {ChromiumPackageSha256}{Environment.NewLine}" +
                        $"실제: {actualHash}");
            }

            Directory.CreateDirectory(stagingDirectory);
            ZipFile.ExtractToDirectory(packagePath, stagingDirectory, overwriteFiles: true);

            var stagedExecutable = Path.Combine(stagingDirectory, "chrome.exe");
            if (!File.Exists(stagedExecutable))
                throw new InvalidDataException("Chromium 패키지에 chrome.exe가 없습니다.");
            if (!IsX86PortableExecutable(stagedExecutable))
                throw new InvalidDataException("다운로드한 Chromium이 x86 실행 파일이 아닙니다.");

            if (Directory.Exists(ChromiumDirectory))
                Directory.Delete(ChromiumDirectory, recursive: true);

            Directory.Move(stagingDirectory, ChromiumDirectory);
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    public static void Open(string swfPath)
    {
        if (!File.Exists(swfPath))
            throw new FileNotFoundException("SWF 파일이 없습니다.", swfPath);
        if (!HasChromium)
            throw new FileNotFoundException(
                "구형 Chromium이 없습니다. 프로그램의 Chromium 다운로드 기능으로 설치해주세요.",
                ChromiumExecutablePath);
        if (!IsChromiumX86)
            throw new InvalidOperationException("SWF 뷰어용 Chromium은 x86 버전이어야 합니다.");
        if (!HasPepperFlash)
            throw new FileNotFoundException(
                "pepflashplayer.dll이 없습니다. Tools\\FlashViewer\\PepperFlash 폴더에 x86 DLL을 직접 넣어주세요.",
                PepperFlashPath);
        if (!IsPepperFlashX86)
            throw new InvalidOperationException("pepflashplayer.dll은 x86 버전이어야 합니다.");

        var sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "DwgConverter",
            "FlashViewer",
            Guid.NewGuid().ToString("N"));
        var profileDirectory = Path.Combine(sessionDirectory, "Profile");
        Directory.CreateDirectory(profileDirectory);

        var htmlPath = Path.Combine(sessionDirectory, "viewer.html");
        File.WriteAllText(htmlPath, BuildWrapperHtml(swfPath), new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = ChromiumExecutablePath,
            WorkingDirectory = ChromiumDirectory,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--disable-default-apps");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-translate");
        startInfo.ArgumentList.Add("--allow-file-access-from-files");
        startInfo.ArgumentList.Add("--allow-outdated-plugins");
        startInfo.ArgumentList.Add("--plugin-policy=allow");
        startInfo.ArgumentList.Add($"--ppapi-flash-path={PepperFlashPath}");
        startInfo.ArgumentList.Add($"--ppapi-flash-version={ReadPepperVersion()}");
        startInfo.ArgumentList.Add("--window-size=1200,850");
        startInfo.ArgumentList.Add($"--app={new Uri(htmlPath).AbsoluteUri}");

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("구형 Chromium SWF 뷰어를 시작하지 못했습니다.");
    }

    private static bool IsX86PortableExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 64)
                return false;

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset + 6 > stream.Length)
                return false;

            stream.Position = peOffset + 4;
            return reader.ReadUInt16() == 0x014c;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildWrapperHtml(string swfPath)
    {
        var swfUri = WebUtility.HtmlEncode(new Uri(Path.GetFullPath(swfPath)).AbsoluteUri);
        return $@"<!doctype html>
<html>
<head>
<meta charset=""utf-8"">
<title>SWF Viewer</title>
<style>
html, body {{ width:100%; height:100%; margin:0; overflow:hidden; background:#202020; }}
object {{ width:100%; height:100%; display:block; }}
</style>
</head>
<body>
<object type=""application/x-shockwave-flash"" data=""{swfUri}"">
  <param name=""movie"" value=""{swfUri}"">
  <param name=""allowScriptAccess"" value=""never"">
  <param name=""allowNetworking"" value=""none"">
  <param name=""wmode"" value=""opaque"">
</object>
</body>
</html>
";
    }

    private static string ReadPepperVersion()
    {
        try
        {
            if (!File.Exists(PepperManifestPath))
                return "99.99.99.99";

            using var document = JsonDocument.Parse(File.ReadAllText(PepperManifestPath));
            if (document.RootElement.TryGetProperty("version", out var version))
            {
                var value = version.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // Chromium documents 99.99.99.99 as a usable fallback when the matching
            // Pepper Flash version is not supplied.
        }

        return "99.99.99.99";
    }
}
