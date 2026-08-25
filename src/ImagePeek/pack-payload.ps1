param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$PreviewDll,
    [Parameter(Mandatory = $true)][string]$CoreDir
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 涓?GZipStream 闇€瑕佷粠 GAC 鏄惧紡鍔犺浇
$script:CanGzip = $false
try {
    $gacDll = Join-Path $env:windir 'Microsoft.NET\assembly\GAC_MSIL\System.IO.Compression\v4.0_4.0.0.0__b77a5c561934e089\System.IO.Compression.dll'
    if (Test-Path -LiteralPath $gacDll) {
        Add-Type -Path $gacDll
        $script:CanGzip = $true
    }
}
catch {
    $script:CanGzip = $false
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem -LiteralPath $OutDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

$managed = New-Object System.Collections.Generic.List[string]
$native = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $PreviewDll)) { throw "PreviewDll not found: $PreviewDll" }
if (-not (Test-Path -LiteralPath $CoreDir)) { throw "CoreDir not found: $CoreDir" }

$managed.Add($PreviewDll)

Get-ChildItem -LiteralPath $CoreDir -Filter *.dll -File | ForEach-Object {
    $isManaged = $false
    try {
        [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null
        $isManaged = $true
    }
    catch {
        $isManaged = $false
    }
    if ($isManaged) { $managed.Add($_.FullName) } else { $native.Add($_.FullName) }
}

$nativeDir = Join-Path $CoreDir 'native'
if (Test-Path -LiteralPath $nativeDir) {
    Get-ChildItem -LiteralPath $nativeDir -Filter *.dll -File | ForEach-Object {
        $native.Add($_.FullName)
    }
}

$entries = New-Object System.Collections.Generic.List[string]

function Pack([string]$path, [string]$resName) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $out = Join-Path $OutDir ($resName + '.gz')
    if ($script:CanGzip) {
        $ms = New-Object System.IO.MemoryStream
        $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        $gz.Write($bytes, 0, $bytes.Length)
        $gz.Dispose()
        [System.IO.File]::WriteAllBytes($out, $ms.ToArray())
        $ms.Dispose()
    }
    else {
        [System.IO.File]::WriteAllBytes($out, $bytes)
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    $script:entries.Add("$resName|$($bytes.Length)|$hash")
}

function PackFromString([string]$resName, [string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $out = Join-Path $OutDir ($resName + '.gz')
    if ($script:CanGzip) {
        $ms = New-Object System.IO.MemoryStream
        $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        $gz.Write($bytes, 0, $bytes.Length)
        $gz.Dispose()
        [System.IO.File]::WriteAllBytes($out, $ms.ToArray())
        $ms.Dispose()
    }
    else {
        [System.IO.File]::WriteAllBytes($out, $bytes)
    }
}

foreach ($p in $managed) { Pack $p ([System.IO.Path]::GetFileName($p)) }
foreach ($p in $native) { Pack $p ('native__' + [System.IO.Path]::GetFileName($p)) }

$joined = [string]::Join("`n", $entries.ToArray())
$sha2 = [System.Security.Cryptography.SHA256]::Create()
try {
    $version = [BitConverter]::ToString($sha2.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($joined))).Replace('-', '').ToLowerInvariant().Substring(0, 12)
}
finally {
    $sha2.Dispose()
}
PackFromString 'version.txt' $version

Write-Host "PackPayload: $($managed.Count) managed + $($native.Count) native -> $OutDir (version $version)"
exit 0

