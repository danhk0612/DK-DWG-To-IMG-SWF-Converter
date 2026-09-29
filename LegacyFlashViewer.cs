using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DwgToPngPoC;

internal static class LegacyFlashViewer
{
    public static string ToolDirectory => Path.Combine(AppContext.BaseDirectory, "Tools", "FlashViewer");
    public static string ChromiumDirectory => Path.Combine(ToolDirectory, "Chromium");
    public static string ChromiumExecutablePath => Path.Combine(ChromiumDirectory, "chrome.exe");
    public static string PepperDirectory => Path.Combine(ToolDirectory, "PepperFlash");
    public static string PepperFlashPath => Path.Combine(PepperDirectory, "pepflashplayer.dll");
    public static string PepperManifestPath => Path.Combine(PepperDirectory, "manifest.json");

    public static bool IsAvailable => File.Exists(ChromiumExecutablePath) && File.Exists(PepperFlashPath);

    public static string StatusText
    {
        get
        {
            if (!File.Exists(ChromiumExecutablePath) && !File.Exists(PepperFlashPath))
                return "Chromium / Pepper Flash 없음";
            if (!File.Exists(ChromiumExecutablePath))
                return "Chromium 없음";
            if (!File.Exists(PepperFlashPath))
                return "pepflashplayer.dll 없음";
            return "사용 가능";
        }
    }

    public static void Open(string swfPath)
    {
        if (!File.Exists(swfPath))
            throw new FileNotFoundException("SWF 파일이 없습니다.", swfPath);
        if (!File.Exists(ChromiumExecutablePath))
            throw new FileNotFoundException(
                "구형 Chromium이 없습니다. Tools\\FlashViewer\\Chromium 폴더에 chrome.exe와 함께 필요한 Chromium 파일을 넣어주세요.",
                ChromiumExecutablePath);
        if (!File.Exists(PepperFlashPath))
            throw new FileNotFoundException(
                "pepflashplayer.dll이 없습니다. Tools\\FlashViewer\\PepperFlash 폴더에 직접 넣어주세요.",
                PepperFlashPath);

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
