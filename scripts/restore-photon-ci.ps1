#requires -Version 7.0
<#
Restore the licensed SDK for a trusted CI run without publishing its plaintext.

Bundle v1: ASCII "PHOTON-SDK-CI-1\n" (16 bytes), nonce (12 bytes), GCM tag
(16 bytes), then ciphertext. The header is additional authenticated data.
PHOTON_SDK_CI_KEY is the base64 encoding of a 32-byte AES key, supplied only
through the environment. The authenticated plaintext is a ZIP containing exactly
photon-unity-sdk_v5-1-21.zip and oss-.net_client_sdks.pdf at its root.

The existing importer verifies the pinned vendor hashes and every imported file.
This script does not download, connect to Photon, or read application credentials.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $BundlePath,
    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $PythonCommand = 'python',
    [string] $TemporaryRoot = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temporaryDirectory = $null
$key = $null
$plaintext = $null
$cipher = $null
$zip = $null
$stream = $null

try {
    if ([string]::IsNullOrWhiteSpace($env:PHOTON_SDK_CI_KEY)) {
        throw 'PHOTON_SDK_CI_KEY is missing. Configure the repository Actions secret and run a trusted branch. Fork pull requests do not receive this secret; review and run the change on a trusted branch. Do not use pull_request_target to run untrusted code with this key.'
    }
    try {
        $key = [Convert]::FromBase64String($env:PHOTON_SDK_CI_KEY)
    } catch {
        throw 'PHOTON_SDK_CI_KEY must be a base64-encoded 32-byte key.'
    }
    if ($key.Length -ne 32) {
        throw 'PHOTON_SDK_CI_KEY must be a base64-encoded 32-byte key.'
    }

    $bundleFile = Get-Item -LiteralPath $BundlePath
    if ($bundleFile.PSIsContainer -or $bundleFile.Length -le 44 -or $bundleFile.Length -gt 32MB) {
        throw 'Photon CI bundle size is invalid.'
    }
    [byte[]] $header = [Text.Encoding]::ASCII.GetBytes("PHOTON-SDK-CI-1`n")
    [byte[]] $bundle = [IO.File]::ReadAllBytes($bundleFile.FullName)
    for ($index = 0; $index -lt $header.Length; $index++) {
        if ($bundle[$index] -ne $header[$index]) {
            throw 'Photon CI bundle header or version is invalid.'
        }
    }
    [byte[]] $nonce = $bundle[16..27]
    [byte[]] $tag = $bundle[28..43]
    [byte[]] $ciphertext = [byte[]]::new($bundle.Length - 44)
    [Buffer]::BlockCopy($bundle, 44, $ciphertext, 0, $ciphertext.Length)
    $plaintext = [byte[]]::new($ciphertext.Length)
    $cipher = [Security.Cryptography.AesGcm]::new($key)
    try {
        $cipher.Decrypt($nonce, $ciphertext, $tag, $plaintext, $header)
    } catch {
        throw 'Photon CI bundle authentication failed: the key is wrong or the bundle has changed.'
    }

    # Validate the complete entry set before creating any plaintext files.
    $stream = [IO.MemoryStream]::new($plaintext, $false)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    $expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void] $expectedNames.Add('photon-unity-sdk_v5-1-21.zip')
    [void] $expectedNames.Add('oss-.net_client_sdks.pdf')
    if ($zip.Entries.Count -ne $expectedNames.Count) {
        throw 'Photon CI bundle must contain exactly the pinned SDK archive and OSS notices.'
    }
    [long] $expandedSize = 0
    foreach ($entry in $zip.Entries) {
        if (-not $expectedNames.Remove($entry.FullName) -or $entry.Length -le 0) {
            throw 'Photon CI bundle contains an unexpected, duplicate, or empty ZIP entry.'
        }
        $expandedSize += $entry.Length
        if ($expandedSize -gt 32MB) {
            throw 'Photon CI bundle expanded size exceeds the limit.'
        }
    }

    $source = Get-Item -LiteralPath $SourceRoot
    $temporaryParent = Get-Item -LiteralPath $TemporaryRoot
    if (-not $source.PSIsContainer -or -not $temporaryParent.PSIsContainer -or
        $source.PSProvider.Name -ne 'FileSystem' -or $temporaryParent.PSProvider.Name -ne 'FileSystem') {
        throw 'SourceRoot and TemporaryRoot must be existing filesystem directories.'
    }
    $parentPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($temporaryParent.FullName))
    $temporaryDirectory = Join-Path $parentPath ('photon-sdk-ci-' + [Guid]::NewGuid().ToString('N'))
    [void] [IO.Directory]::CreateDirectory($temporaryDirectory)
    foreach ($entry in $zip.Entries) {
        $inputStream = $entry.Open()
        $outputStream = $null
        try {
            $outputStream = [IO.File]::Open((Join-Path $temporaryDirectory $entry.FullName), [IO.FileMode]::CreateNew)
            $inputStream.CopyTo($outputStream)
        } finally {
            if ($null -ne $outputStream) { $outputStream.Dispose() }
            $inputStream.Dispose()
        }
    }
    $zip.Dispose()
    $zip = $null
    $stream.Dispose()
    $stream = $null

    & $PythonCommand (Join-Path $PSScriptRoot 'photon-sdk.py') `
        --archive (Join-Path $temporaryDirectory 'photon-unity-sdk_v5-1-21.zip') `
        --oss-notices (Join-Path $temporaryDirectory 'oss-.net_client_sdks.pdf') `
        --source $source.FullName
    if ($LASTEXITCODE -ne 0) {
        throw 'The Photon SDK importer rejected the bundle; pinned vendor hashes and manifest must match.'
    }
} finally {
    if ($null -ne $zip) { $zip.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
    if ($null -ne $cipher) { $cipher.Dispose() }
    if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    if ($null -ne $key) { [Array]::Clear($key, 0, $key.Length) }
    if ($null -ne $temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        # Never recursively remove a caller-supplied directory. Only the unique
        # child created above may be removed, and check its final resolved path.
        $resolved = (Resolve-Path -LiteralPath $temporaryDirectory).ProviderPath
        if ([IO.Path]::GetFullPath($resolved) -ne [IO.Path]::GetFullPath($temporaryDirectory) -or
            [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetDirectoryName($resolved)) -ne $parentPath -or
            [IO.Path]::GetFileName($resolved) -notmatch '^photon-sdk-ci-[0-9a-f]{32}$') {
            throw 'Refusing cleanup outside the generated Photon SDK temporary directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
