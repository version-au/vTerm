# Fetches libghostty-vt's Windows x64 DLL into bench\native, checked against the release's SHA256SUMS.
param([string]$Version = '1.0.1')
$ErrorActionPreference = 'Stop'
$base = "https://release.files.ghostty.org/libghostty-vt/$Version/"
$zip = "ghostty-vt-$Version-x86_64-windows-gnu.zip"
$native = Join-Path $PSScriptRoot 'native'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ghostty-vt-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp, $native | Out-Null
try {
    Invoke-WebRequest ($base + $zip) -OutFile (Join-Path $tmp $zip) -UseBasicParsing
    $sums = (Invoke-WebRequest ($base + 'SHA256SUMS') -UseBasicParsing).Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
    $want = ($sums -split "`n" | Where-Object { $_ -match [regex]::Escape($zip) + '\s*$' } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    $got = (Get-FileHash (Join-Path $tmp $zip) -Algorithm SHA256).Hash.ToLower()
    if (-not $want -or $got -ne $want.ToLower()) { throw "SHA-256 doesn't match SHA256SUMS ($got vs $want)" }
    Expand-Archive (Join-Path $tmp $zip) (Join-Path $tmp 'x') -Force
    $root = Get-ChildItem (Join-Path $tmp 'x') -Directory | Select-Object -First 1
    Copy-Item (Join-Path $root.FullName 'bin\ghostty-vt.dll'), (Join-Path $root.FullName 'bin\ghostty-vt.pdb'), (Join-Path $root.FullName 'LICENSE') $native -Force
    Copy-Item (Join-Path $root.FullName 'include') $native -Recurse -Force
    "libghostty-vt $Version in $native (sha256 $got)"
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
