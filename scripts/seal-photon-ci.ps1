#requires -Version 7.0
<#
Create the protected CI dependency bundle from the two pinned vendor downloads.
PHOTON_SDK_CI_KEY must contain a base64-encoded, random 32-byte AES key. Store
that value separately as a repository Actions secret; never commit it.

Format: ASCII "PHOTON-SDK-CI-1\n", 12-byte nonce, 16-byte GCM tag, ciphertext.
The header is authenticated additional data. The plaintext ZIP contains only
the official SDK archive and OSS notices, never application configuration.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$OssNoticesPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$keyBytes = $null
$archiveBytes = $null
$ossBytes = $null
$plainBytes = $null
$cipherBytes = $null
$plainStream = $null
$cipher = $null
$output = $null

try {
    if ([string]::IsNullOrWhiteSpace($env:PHOTON_SDK_CI_KEY)) {
        throw 'PHOTON_SDK_CI_KEY is required; provide the key through the environment, not a command argument.'
    }
    try { $keyBytes = [Convert]::FromBase64String($env:PHOTON_SDK_CI_KEY) }
    catch { throw 'PHOTON_SDK_CI_KEY must be base64-encoded 32-byte key material.' }
    if ($keyBytes.Length -ne 32) { throw 'PHOTON_SDK_CI_KEY must decode to exactly 32 bytes.' }

    $destination = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $destination) { throw 'Refusing to overwrite an existing bundle.' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) {
        throw 'The bundle output directory must already exist.'
    }

    # Use the importer's existing immutable pins rather than maintaining a
    # second set of download hashes. No vendor code is executed here.
    $importer = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'photon-sdk.py'))
    $pins = @{}
    foreach ($name in @('ARCHIVE_SHA256', 'OSS_SHA256', 'VERSION')) {
        $match = [regex]::Match($importer, ('(?m)^' + $name + ' = "([a-zA-Z0-9.]+)"\r?$'))
        if (-not $match.Success) { throw "Cannot read the pinned SDK $name from photon-sdk.py." }
        $pins[$name] = $match.Groups[1].Value
    }
    foreach ($path in @($ArchivePath, $OssNoticesPath)) {
        $item = Get-Item -LiteralPath $path
        if ($item.PSIsContainer -or $item.Length -gt 20MB) {
            throw 'Expected a vendor input file smaller than 20 MiB.'
        }
    }
    $archiveBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ArchivePath))
    $ossBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($OssNoticesPath))
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $archiveHash = [BitConverter]::ToString($sha.ComputeHash($archiveBytes)).Replace('-', '').ToLowerInvariant()
        $ossHash = [BitConverter]::ToString($sha.ComputeHash($ossBytes)).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
    if ($archiveHash -ne $pins.ARCHIVE_SHA256 -or $ossHash -ne $pins.OSS_SHA256) {
        throw 'Official Photon archive or OSS notices do not match the pinned SHA256.'
    }

    $plainStream = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($plainStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $archiveName = 'photon-unity-sdk_v' + $pins.VERSION.Replace('.', '-') + '.zip'
        $inputs = @(
            @{ Name = $archiveName; Bytes = $archiveBytes },
            @{ Name = 'oss-.net_client_sdks.pdf'; Bytes = $ossBytes }
        )
        foreach ($inputFile in $inputs) {
            $entry = $zip.CreateEntry($inputFile.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entryStream = $entry.Open()
            try { $entryStream.Write($inputFile.Bytes, 0, $inputFile.Bytes.Length) }
            finally { $entryStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $plainBytes = $plainStream.ToArray()
    $header = [Text.Encoding]::ASCII.GetBytes("PHOTON-SDK-CI-1`n")
    $nonce = [byte[]]::new(12)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($nonce) } finally { $rng.Dispose() }
    $tag = [byte[]]::new(16)
    $cipherBytes = [byte[]]::new($plainBytes.Length)
    $cipher = [Security.Cryptography.AesGcm]::new($keyBytes)
    $cipher.Encrypt($nonce, $plainBytes, $cipherBytes, $tag, $header)

    # CreateNew also prevents an overwrite if another process creates the path
    # after the initial check. Only authenticated ciphertext is written.
    $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    foreach ($part in @($header, $nonce, $tag, $cipherBytes)) { $output.Write($part, 0, $part.Length) }
    $output.Dispose()
    $output = $null
    Write-Output 'Pinned Photon SDK and notices sealed for CI; no application configuration included.'
} finally {
    if ($null -ne $output) { $output.Dispose() }
    if ($null -ne $cipher) { $cipher.Dispose() }
    foreach ($buffer in @($keyBytes, $archiveBytes, $ossBytes, $plainBytes, $cipherBytes)) {
        if ($null -ne $buffer) { [Array]::Clear($buffer, 0, $buffer.Length) }
    }
    if ($null -ne $plainStream) {
        $buffer = $plainStream.GetBuffer()
        [Array]::Clear($buffer, 0, $buffer.Length)
        $plainStream.Dispose()
    }
}
