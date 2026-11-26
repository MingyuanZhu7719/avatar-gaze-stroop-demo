#requires -Version 7.0
<#
Exercise authenticated restoration and pinned import on a fresh temporary source.
Requires the real encrypted bundle and PHOTON_SDK_CI_KEY for the positive case.
All negative fixtures use unrelated random keys; no key or plaintext is logged.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $BundlePath,
    [string] $PythonCommand = 'python'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:PHOTON_SDK_CI_KEY)) {
    throw 'Tests require the repository Actions secret PHOTON_SDK_CI_KEY and the real encrypted SDK bundle. Fork pull requests do not receive this secret; review and run the change on a trusted branch. Do not use pull_request_target to run untrusted code with this key.'
}
$realBundle = (Get-Item -LiteralPath $BundlePath).FullName
$realKey = $env:PHOTON_SDK_CI_KEY
$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testParent ('photon-ci-tests-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($testRoot)
$restoreTemp = Join-Path $testRoot 'restore-temp'
[void] [IO.Directory]::CreateDirectory($restoreTemp)
$restoreScript = Join-Path $PSScriptRoot 'restore-photon-ci.ps1'
$pwsh = (Get-Process -Id $PID).Path
$passed = 0

function New-RandomBytes([int] $Count) {
    $result = [byte[]]::new($Count)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($result) } finally { $random.Dispose() }
    return ,$result
}

function Write-TestBundle([string] $Path, [byte[]] $Key, [object[]] $Entries) {
    $buffer = [IO.MemoryStream]::new()
    $archive = [IO.Compression.ZipArchive]::new($buffer, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($item in $Entries) {
            $entry = $archive.CreateEntry($item.Name)
            $entryStream = $entry.Open()
            try {
                [byte[]] $bytes = [Text.Encoding]::UTF8.GetBytes($item.Content)
                $entryStream.Write($bytes, 0, $bytes.Length)
            } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
    [byte[]] $payload = $buffer.ToArray()
    $buffer.Dispose()
    [byte[]] $header = [Text.Encoding]::ASCII.GetBytes("PHOTON-SDK-CI-1`n")
    [byte[]] $nonce = New-RandomBytes 12
    [byte[]] $tag = [byte[]]::new(16)
    [byte[]] $encrypted = [byte[]]::new($payload.Length)
    $aes = [Security.Cryptography.AesGcm]::new($Key)
    try { $aes.Encrypt($nonce, $payload, $encrypted, $tag, $header) } finally { $aes.Dispose() }
    [IO.File]::WriteAllBytes($Path, [byte[]] ($header + $nonce + $tag + $encrypted))
    [Array]::Clear($payload, 0, $payload.Length)
}

function Invoke-RestoreCase([string] $Name, [string] $Bundle, [string] $Key,
                            [string] $ExpectedError, [string] $Source = '') {
    if (-not $Source) {
        $Source = Join-Path $testRoot ('source-' + [Guid]::NewGuid().ToString('N'))
        [void] [IO.Directory]::CreateDirectory($Source)
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $pwsh
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $restoreScript, '-BundlePath', $Bundle,
            '-SourceRoot', $Source, '-PythonCommand', $PythonCommand, '-TemporaryRoot', $restoreTemp)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['PHOTON_SDK_CI_KEY'] = $Key
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void] $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Case '$Name' timed out."
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        # PowerShell's concise error renderer wraps messages with a `|` gutter.
        # Match diagnostics independently of ANSI styling or console width.
        $output = [regex]::Replace($output, '\x1B\[[0-?]*[ -/]*[@-~]', '')
        $output = [regex]::Replace($output, '\r?\n\s*\|\s*', ' ')
        if (($Key -and $output.Contains($Key)) -or $output.Contains($realKey)) {
            throw "Case '$Name' leaked a key in process output."
        }
        if ($ExpectedError) {
            if ($process.ExitCode -eq 0 -or -not $output.Contains($ExpectedError)) {
                throw "Case '$Name' did not fail with the required diagnostic. Exit: $($process.ExitCode). Output: $output"
            }
            if (Test-Path -LiteralPath (Join-Path $Source 'Assets/Photon')) {
                throw "Case '$Name' installed SDK files despite rejection."
            }
        } elseif ($process.ExitCode -ne 0) {
            throw "Case '$Name' failed. Exit: $($process.ExitCode). Output: $output"
        } elseif (-not (Test-Path -LiteralPath (Join-Path $Source 'Assets/Photon/sdk-manifest.json'))) {
            throw "Case '$Name' did not install the verified real SDK."
        }
        if (@(Get-ChildItem -LiteralPath $restoreTemp -Force).Count -ne 0) {
            throw "Case '$Name' left decrypted files in temporary storage."
        }
        $script:passed++
        Write-Host "PASS: $Name"
    } finally { $process.Dispose() }
}

try {
    [byte[]] $fixtureKey = New-RandomBytes 32
    $fixtureKeyText = [Convert]::ToBase64String($fixtureKey)
    $fixture = Join-Path $testRoot 'synthetic.aesgcm'
    $validNames = @(
        @{Name = 'photon-unity-sdk_v5-1-21.zip'; Content = 'not the licensed SDK'},
        @{Name = 'oss-.net_client_sdks.pdf'; Content = 'not the pinned notices'}
    )
    Write-TestBundle $fixture $fixtureKey $validNames
    Invoke-RestoreCase 'Missing key explains trusted-run requirement' $fixture '' 'PHOTON_SDK_CI_KEY is missing'
    Invoke-RestoreCase 'Malformed key rejected' $fixture 'not-base64' 'base64-encoded 32-byte key'
    $wrongKey = [Convert]::ToBase64String((New-RandomBytes 32))
    Invoke-RestoreCase 'Wrong key rejected before extraction' $fixture $wrongKey 'authentication failed'

    $tampered = Join-Path $testRoot 'tampered.aesgcm'
    [byte[]] $changed = [IO.File]::ReadAllBytes($fixture)
    $changed[$changed.Length - 1] = $changed[$changed.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $changed)
    Invoke-RestoreCase 'Ciphertext tampering rejected' $tampered $fixtureKeyText 'authentication failed'
    $changed[0] = $changed[0] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $changed)
    Invoke-RestoreCase 'Unknown bundle header rejected' $tampered $fixtureKeyText 'header or version is invalid'

    $unexpected = Join-Path $testRoot 'unexpected.aesgcm'
    Write-TestBundle $unexpected $fixtureKey @(
        @{Name = '../photon-unity-sdk_v5-1-21.zip'; Content = 'unsafe path'}, $validNames[1])
    Invoke-RestoreCase 'ZIP path traversal rejected' $unexpected $fixtureKeyText 'unexpected, duplicate, or empty'
    Write-TestBundle $unexpected $fixtureKey @($validNames[0], $validNames[0])
    Invoke-RestoreCase 'Duplicate ZIP entry rejected' $unexpected $fixtureKeyText 'unexpected, duplicate, or empty'
    Write-TestBundle $unexpected $fixtureKey @($validNames[0], $validNames[1],
        @{Name = 'photon-woz.local.json'; Content = 'must never be included'})
    Invoke-RestoreCase 'Additional configuration file rejected' $unexpected $fixtureKeyText 'exactly the pinned SDK archive'
    Invoke-RestoreCase 'Authenticated but unpinned vendor bytes rejected' $fixture $fixtureKeyText 'pinned vendor hashes'

    $successSource = Join-Path $testRoot 'real-source'
    [void] [IO.Directory]::CreateDirectory($successSource)
    Invoke-RestoreCase 'Real SDK imported and manifest verified in a clean source' $realBundle $realKey '' $successSource
    Invoke-RestoreCase 'Existing SDK verified on repeat restoration' $realBundle $realKey '' $successSource
    Write-Host "Photon CI restore: $passed checks passed; real SDK imported without a network connection."
} finally {
    if ($null -ne (Get-Variable fixtureKey -ErrorAction SilentlyContinue)) {
        [Array]::Clear($fixtureKey, 0, $fixtureKey.Length)
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).ProviderPath
        if ([IO.Path]::GetFullPath($resolved) -ne [IO.Path]::GetFullPath($testRoot) -or
            [IO.Path]::GetDirectoryName($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar) -ne
                $testParent.TrimEnd([IO.Path]::DirectorySeparatorChar) -or
            [IO.Path]::GetFileName($resolved) -notmatch '^photon-ci-tests-[0-9a-f]{32}$') {
            throw 'Refusing cleanup outside the generated Photon CI test directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
