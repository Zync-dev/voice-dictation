<#
.SYNOPSIS
    Builds Murmur's installer and update packages with Velopack.

.DESCRIPTION
    1. dotnet publish: self-contained win-x64, ReadyToRun (faster start), into publish-installer\.
    2. Checks that the speech runtimes (whisper.cpp for CPU and Vulkan) made it into the publish folder.
    3. vpk pack: Releases\Murmur-win-Setup.exe, the portable zip, the full (and, given an older release
       in Releases\, delta) .nupkg and releases.win.json that installed copies read to find updates.

    Nothing is uploaded. To publish a release on GitHub afterwards (the default update source):
        dotnet vpk upload github --repoUrl https://github.com/Zync-dev/voice-dictation --token <token> --publish --releaseName "Murmur <version>" --tag v<version>
    Before packing a new version, "dotnet vpk download github --repoUrl ... --token ..." fetches the previous
    release into Releases\ so vpk can make a small delta update from it.

.PARAMETER Version
    The version to build, e.g. 1.0.1. Defaults to <Version> in Murmur\Murmur.csproj. Every release needs a higher one.

.PARAMETER NoReadyToRun
    Publish without ReadyToRun (smaller, slower to start), if a ReadyToRun build ever misbehaves.
#>
param(
    [string]$Version,
    [switch]$NoReadyToRun,
    [string]$OutputDir = "Releases"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "Murmur\Murmur.csproj"
$publish = Join-Path $root "publish-installer"
$output = [IO.Path]::Combine($root, $OutputDir) # an absolute path is used as it is

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building Murmur $Version" -ForegroundColor Cyan

# 1. Publish
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$r2r = if ($NoReadyToRun) { "false" } else { "true" }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishReadyToRun=$r2r -p:Version=$Version -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The speech packages carry Linux, ARM and 32-bit builds too (about 65 MB); a win-x64 install needs none of them.
foreach ($extra in "runtimes\vulkan\linux-x64", "runtimes\win-arm64", "runtimes\win-x86", "runtimes\linux-x64", "runtimes\osx-x64", "runtimes\osx-arm64") {
    $path = Join-Path $publish $extra
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

# 2. The parts Murmur cannot work without
$required = @(
    "Murmur.exe",
    "Velopack.dll",
    "runtimes\win-x64\whisper.dll",
    "runtimes\vulkan\win-x64\whisper.dll",
    "runtimes\vulkan\win-x64\ggml-vulkan-whisper.dll"
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missing) {
    Write-Host "Files in the publish folder's runtimes:" -ForegroundColor Yellow
    Get-ChildItem (Join-Path $publish "runtimes") -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($publish.Length + 1) }
    throw "Missing from the publish folder: $($missing -join ', ')"
}
# Fonts are compiled into Murmur.dll as WPF resources (names stored as UTF-16); check they are there.
$dll = [IO.File]::ReadAllBytes((Join-Path $publish "Murmur.dll"))
$text = [Text.Encoding]::Unicode.GetString($dll) + [Text.Encoding]::Unicode.GetString($dll, 1, $dll.Length - 1)
foreach ($font in "bricolagegrotesque-bold.ttf", "figtree-regular.ttf", "jetbrainsmono-regular.ttf") {
    if (-not $text.Contains("fonts/$font")) { throw "Font $font is not inside Murmur.dll" }
}

# 3. Pack
dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (vpk is a local tool, see dotnet-tools.json)" }
New-Item -ItemType Directory -Force $output | Out-Null
dotnet vpk pack `
    --packId Murmur `
    --packVersion $Version `
    --runtime win-x64 `
    --packDir $publish `
    --mainExe Murmur.exe `
    --packTitle Murmur `
    --packAuthors Murmur `
    --icon (Join-Path $root "Murmur\Assets\murmur.ico") `
    --shortcuts StartMenuRoot `
    --outputDir $output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host ""
Write-Host "Done. In $output :" -ForegroundColor Green
Get-ChildItem $output -File | Sort-Object LastWriteTime -Descending | Select-Object -First 6 |
    ForEach-Object { "  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
