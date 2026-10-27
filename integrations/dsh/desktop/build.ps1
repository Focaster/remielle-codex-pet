# Build the desktop pet (remielle-pet.exe).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Uses the .NET Framework compiler that ships with Windows (csc.exe).
# No SDK, no Electron, no NuGet. Requires .NET Framework 4.x at runtime.
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'Pet.cs'
$output = Join-Path $here 'remielle-pet.exe'

if (-not (Test-Path $source)) { throw "Pet.cs not found: $source" }

# Locate csc.exe: 64-bit first, then 32-bit.
$frameworks = @(
  "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319",
  "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
)
$fw = $frameworks | Where-Object { Test-Path (Join-Path $_ 'csc.exe') } | Select-Object -First 1
if (-not $fw) { throw 'csc.exe not found - .NET Framework 4.x is required' }

$csc = Join-Path $fw 'csc.exe'
$refs = @(
  (Join-Path $fw 'WPF\PresentationFramework.dll'),
  (Join-Path $fw 'WPF\PresentationCore.dll'),
  (Join-Path $fw 'WPF\WindowsBase.dll'),
  (Join-Path $fw 'System.Xaml.dll'),
  (Join-Path $fw 'System.dll'),
  (Join-Path $fw 'System.Core.dll'),
  (Join-Path $fw 'System.Windows.Forms.dll'),
  (Join-Path $fw 'System.Drawing.dll')
)
foreach ($ref in $refs) {
  if (-not (Test-Path $ref)) { throw "Missing reference assembly: $ref" }
}

$cscArgs = @('/nologo', '/codepage:65001', '/target:winexe', "/out:$output") +
  ($refs | ForEach-Object { "/r:$_" }) + @($source)

Write-Host "csc: $csc"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$exe = Get-Item $output
Write-Host ("OK  {0}  ({1:N1} KB)" -f $exe.Name, ($exe.Length / 1KB))
Write-Host "Note: frames\ must exist first - run scripts/link-assets.mjs"
