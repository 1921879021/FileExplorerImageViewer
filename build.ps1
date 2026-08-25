param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==> [1/4] Build ImagePeek.Core / ImagePeek.Preview" -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\ImagePeek.Core\ImagePeek.Core.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }
dotnet build (Join-Path $root 'src\ImagePeek.Preview\ImagePeek.Preview.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed' }

Write-Host "==> [2/4] Pack decode payload (Preview DLL + libvips)" -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\ImagePeek\pack-payload.ps1') `
    -OutDir (Join-Path $root 'src\ImagePeek\obj\payload') `
    -PreviewDll (Join-Path $root "src\ImagePeek.Preview\bin\$Configuration\net48\ImagePeek.Preview.dll") `
    -CoreDir (Join-Path $root "src\ImagePeek.Core\bin\$Configuration\net48")
if ($LASTEXITCODE -ne 0) { throw 'payload pack failed' }

Write-Host "==> [3/4] Build ImagePeek.exe (single file, payload embedded)" -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\ImagePeek\ImagePeek.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'main app build failed' }

Write-Host "==> [4/4] Build ImagePeek.TestHost" -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\ImagePeek.TestHost\ImagePeek.TestHost.csproj') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'TestHost build failed' }

Write-Host ""
Write-Host "BUILD OK:" -ForegroundColor Green
$exe = Get-Item (Join-Path $root "src\ImagePeek\bin\$Configuration\net48\ImagePeek.exe")
Write-Host ("  exe : {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
