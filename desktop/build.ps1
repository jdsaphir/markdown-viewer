# ---------------------------------------------------------------------------
# Builds MarkdownViewer.exe using the C# compiler that ships with Windows.
# No .NET SDK, Visual Studio, or package manager required.
#
#   powershell -ExecutionPolicy Bypass -File desktop\build.ps1
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = Split-Path -Parent $here
$obj  = Join-Path $here 'obj'
$lib  = Join-Path $here 'lib'
$out  = Join-Path $repo 'MarkdownViewer.exe'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
  throw "Could not find the .NET Framework C# compiler at $csc. Windows 10/11 ships with it; check that .NET Framework 4.x is enabled."
}

New-Item -ItemType Directory -Force -Path $obj | Out-Null
New-Item -ItemType Directory -Force -Path $lib | Out-Null

# --- 1. WebView2 SDK -------------------------------------------------------
$wv2Version = '1.0.4191.47'
$core       = Join-Path $lib 'Microsoft.Web.WebView2.Core.dll'
$winforms   = Join-Path $lib 'Microsoft.Web.WebView2.WinForms.dll'
$loader     = Join-Path $lib 'WebView2Loader.dll'

if (-not ((Test-Path $core) -and (Test-Path $winforms) -and (Test-Path $loader))) {
  Write-Host "Fetching the WebView2 SDK ($wv2Version) from nuget.org..."
  $nupkg = Join-Path $obj 'webview2.zip'
  $stage = Join-Path $obj 'webview2'
  $url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wv2Version/microsoft.web.webview2.$wv2Version.nupkg"

  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $stage)

  Copy-Item (Join-Path $stage 'lib\net462\Microsoft.Web.WebView2.Core.dll')     $core     -Force
  Copy-Item (Join-Path $stage 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') $winforms -Force
  Copy-Item (Join-Path $stage 'runtimes\win-x64\native\WebView2Loader.dll')     $loader   -Force

  Remove-Item $stage -Recurse -Force
  Remove-Item $nupkg -Force
}

# --- 2. Application icon ---------------------------------------------------
$ico = Join-Path $obj 'MarkdownViewer.ico'
Write-Host 'Generating the application icon...'
& $csc /nologo /target:exe /platform:anycpu /optimize+ `
       "/out:$(Join-Path $obj 'MakeIcon.exe')" `
       /reference:System.Drawing.dll `
       "$(Join-Path $here 'MakeIcon.cs')"
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the icon generator.' }

& (Join-Path $obj 'MakeIcon.exe') $ico
if ($LASTEXITCODE -ne 0) { throw 'Failed to generate the icon.' }

# --- 3. The application ----------------------------------------------------
# The web app is embedded so a lone .exe still works; copies sitting next to
# the exe take precedence, which keeps `index.html` editable in place.
$webFiles = @(
  'index.html',
  'assets/app.css', 'assets/markdown.css', 'assets/highlight.css',
  'assets/app.js', 'assets/favicon.svg',
  'vendor/marked.min.js', 'vendor/purify.min.js', 'vendor/highlight.min.js'
)

$cscArgs = @(
  '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-',
  "/out:$out",
  "/win32icon:$ico",
  "/win32manifest:$(Join-Path $here 'app.manifest')",
  '/reference:System.dll',
  '/reference:System.Core.dll',
  '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll',
  '/reference:System.Web.Extensions.dll',
  "/reference:$core",
  "/reference:$winforms",
  "/resource:$core,lib.Microsoft.Web.WebView2.Core.dll",
  "/resource:$winforms,lib.Microsoft.Web.WebView2.WinForms.dll",
  "/resource:$loader,lib.WebView2Loader.dll",
  "/resource:$ico,app.icon"
)

foreach ($f in $webFiles) {
  $src = Join-Path $repo ($f -replace '/', '\')
  if (-not (Test-Path $src)) { throw "Missing web asset: $src" }
  $cscArgs += "/resource:$src,web.$($f -replace '/', '.')"
}

$cscArgs += (Join-Path $here 'MarkdownViewer.cs')

Write-Host 'Compiling MarkdownViewer.exe...'
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

$size = [Math]::Round((Get-Item $out).Length / 1MB, 2)
Write-Host ''
Write-Host "Built $out ($size MB)" -ForegroundColor Green
Write-Host 'Run it by double-clicking, or pass a Markdown file:  MarkdownViewer.exe notes.md'
