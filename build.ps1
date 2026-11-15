# Build Weather Wallpaper: Roslyn csc shipped with Visual Studio + .NET Framework 4.8 + WebView2 SDK (NuGet).
#   .\build.ps1                      -> bin\WeatherWallpaper.exe
#   .\build.ps1 -Version 1.2.3 -Msi  -> also dist\WeatherWallpaper-1.2.3-x64.msi and portable zip (needs `wix` dotnet tool v5)
param([string]$Version = '0.0.0', [switch]$Msi)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
$obj = Join-Path $root 'obj'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$WebView2Version = '1.0.1418.22'
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# WebView2 SDK: local NuGet cache first, otherwise download the nupkg into obj\
New-Item -ItemType Directory -Force $out, $obj | Out-Null
$wvRoot = "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2\$WebView2Version"
if (-not (Test-Path $wvRoot)) {
    $wvRoot = Join-Path $obj "webview2.$WebView2Version"
    if (-not (Test-Path $wvRoot)) {
        $zip = Join-Path $obj 'webview2.zip'
        Write-Host "Downloading Microsoft.Web.WebView2 $WebView2Version ..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$WebView2Version" -OutFile $zip -UseBasicParsing
        Expand-Archive $zip $wvRoot -Force
        Remove-Item $zip
    }
}
$wvLib = Join-Path $wvRoot 'lib\net45'

# Version info
$ai = Join-Path $obj 'AssemblyInfo.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("Weather Wallpaper")]
[assembly: AssemblyProduct("Weather Wallpaper")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content $ai -Encoding UTF8

$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Net.Http.dll',
        'System.Device.dll', 'System.Web.Extensions.dll' | ForEach-Object { "/r:$fw\$_" }
$refs += "/r:$wvLib\Microsoft.Web.WebView2.Core.dll", "/r:$wvLib\Microsoft.Web.WebView2.WinForms.dll"

$sources = @((Get-ChildItem "$root\src\*.cs").FullName) + $ai
& $csc /nologo /noconfig /target:winexe /platform:x64 /optimize+ /langversion:latest `
    @refs "/win32manifest:$root\app.manifest" "/out:$out\WeatherWallpaper.exe" @sources
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

Copy-Item "$wvLib\Microsoft.Web.WebView2.Core.dll", "$wvLib\Microsoft.Web.WebView2.WinForms.dll" $out -Force
Copy-Item (Join-Path $wvRoot 'runtimes\win-x64\native\WebView2Loader.dll') $out -Force
Remove-Item "$out\web" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$root\web" "$out\web" -Recurse -Force
Write-Host "OK -> $out\WeatherWallpaper.exe ($Version)"

if ($Msi) {
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $msiPath = Join-Path $dist "WeatherWallpaper-$Version-x64.msi"
    & wix build "$root\installer\Package.wxs" -arch x64 -culture zh-CN -d "Version=$Version" -bindpath "bin=$out" -o $msiPath
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $dist '*.wixpdb') -ErrorAction SilentlyContinue
    $zipPath = Join-Path $dist "WeatherWallpaper-$Version-x64-portable.zip"
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    Compress-Archive "$out\*" $zipPath
    Write-Host "OK -> $msiPath"
    Write-Host "OK -> $zipPath"
}
