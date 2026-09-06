# Replays the customer documentation on native Windows; no source-built server.
param(
    [string]$WorkDirectory = (Get-Location).Path,
    [switch]$ParseOnly
)
$ErrorActionPreference = 'Stop'
$document = Join-Path $PSScriptRoot '../../docs/get-started/windows-packages.md'
$blocks = [regex]::Matches((Get-Content -LiteralPath $document -Raw), '(?s)```powershell\r?\n(.*?)\r?\n```')
if ($blocks.Count -ne 8) { throw 'Customer PowerShell block count changed; review the replay order' }
foreach ($block in $blocks) {
    $parseErrors = $null
    $tokens = $null
    [System.Management.Automation.Language.Parser]::ParseInput(
        $block.Groups[1].Value, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
if ($ParseOnly) { Write-Output 'All customer PowerShell blocks parse'; exit 0 }
if ($env:OS -ne 'Windows_NT') { throw 'This rehearsal requires native Windows and Docker Desktop' }
$originalLocation = (Get-Location).Path
Set-Location -LiteralPath $WorkDirectory
$receipt = [ordered]@{
    kind = 'pre-cut-windows-published-package-rehearsal'
    exactCandidateQualification = $false
    freshInstall = $false
    restartReadback = $false
    recreatedContainerReadback = $false
    cleanup = $false
}
$Project = $null
try {
    # Dot source literal customer blocks so their session variables/functions persist.
    foreach ($index in 0..3) {
        . ([scriptblock]::Create($blocks[$index].Groups[1].Value))
    }
    $receipt.image = $Image
    $receipt.freshInstall = $true
    $receipt.clients = @('honua-admin==0.1.8', 'honua-sdk==0.1.11')
    function Wait-DocumentedReadiness {
        $deadline = (Get-Date).AddMinutes(3)
        do {
            $ready = $false
            try { $ready = (Invoke-WebRequest "$env:HONUA_BASE_URL/healthz/ready" -UseBasicParsing).StatusCode -eq 200 } catch { }
            if (-not $ready) { Start-Sleep -Seconds 2 }
        } until ($ready -or (Get-Date) -ge $deadline)
        if (-not $ready) { throw 'Readiness failed after recovery' }
    }
    dc restart honua
    Wait-DocumentedReadiness
    & $Python journey.py --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Restart readback failed' }
    $receipt.restartReadback = $true
    dc down
    dc up -d --wait --wait-timeout 180
    Wait-DocumentedReadiness
    & $Python journey.py --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Recreated-container readback failed' }
    $receipt.recreatedContainerReadback = $true
} finally {
    # The customer block always generates a new project. Never prune shared Docker state.
    if ($Project -and (Test-Path -LiteralPath compose.yaml)) {
        dc down --volumes
        $receipt.cleanup = $true
    }
    Set-Location -LiteralPath $originalLocation
    $receipt.observedAt = [DateTime]::UtcNow.ToString('o')
    $receipt | ConvertTo-Json | Write-Output
}
