param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

[xml]$project = Get-Content (Join-Path $root "DwgToPngPoC.csproj")
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "DwgToPngPoC.csproj에서 Version을 찾지 못했습니다."
}

$publish = Join-Path $root "publish\$Runtime"
$releaseRoot = Join-Path $root "_release\v$version"
$stage = Join-Path $releaseRoot "DK-DWG-To-IMG-SWF-Converter"
$zip = Join-Path $releaseRoot "DK-DWG-To-IMG-SWF-Converter-v$version-$Runtime.zip"
$shaFile = "$zip.sha256"

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

dotnet restore
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 실패" }

dotnet publish -c $Configuration -r $Runtime --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

Get-ChildItem $publish -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item (Join-Path $publish "*") $stage -Recurse -Force
Copy-Item (Join-Path $root "README.md") $stage -Force
Copy-Item (Join-Path $root "LICENSE") $stage -Force
Copy-Item (Join-Path $root "THIRD_PARTY.md") $stage -Force

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal -Force

$hash = Get-FileHash $zip -Algorithm SHA256
$hashText = $hash.Hash.ToLowerInvariant()
"$hashText  $(Split-Path $zip -Leaf)" | Set-Content $shaFile -Encoding ascii

Write-Host "Release package: $zip"
Write-Host "SHA-256: $hashText"

if ($env:GITHUB_OUTPUT) {
    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "zip=$zip" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "sha_file=$shaFile" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
