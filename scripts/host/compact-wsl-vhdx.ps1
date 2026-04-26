param(
    [string]$UbuntuVhdxPath = "C:\Users\mike\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu20.04LTS_79rhkp1fndgsc\LocalState\ext4.vhdx",
    [switch]$IncludeDocker,
    [string]$DockerVhdxPath = "C:\Users\mike\AppData\Local\Docker\wsl\data\ext4.vhdx"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Format-Bytes {
    param([long]$Bytes)

    if ($Bytes -ge 1TB) { return "{0:N2} TB" -f ($Bytes / 1TB) }
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

function Invoke-VhdxCompact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "VHDX not found: $Path"
        return
    }

    $before = (Get-Item -LiteralPath $Path).Length
    Write-Host ""
    Write-Host "Compacting: $Path"
    Write-Host "Before: $(Format-Bytes $before)"

    $diskpartScript = @"
select vdisk file="$Path"
attach vdisk readonly
compact vdisk
detach vdisk
"@

    $tempFile = Join-Path $env:TEMP ("diskpart-compact-" + [Guid]::NewGuid().ToString("N") + ".txt")
    Set-Content -LiteralPath $tempFile -Value $diskpartScript -Encoding ASCII

    try {
        diskpart /s $tempFile
        if ($LASTEXITCODE -ne 0) {
            throw "diskpart failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    }

    $after = (Get-Item -LiteralPath $Path).Length
    $delta = $before - $after
    Write-Host "After:  $(Format-Bytes $after)"
    if ($delta -gt 0) {
        Write-Host "Freed:  $(Format-Bytes $delta)"
    }
    else {
        Write-Host "Freed:  0 B (no further compaction possible)"
    }
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script in an elevated PowerShell window (Run as Administrator)."
}

Write-Host "Shutting down WSL..."
wsl --shutdown
if ($LASTEXITCODE -ne 0) {
    throw "wsl --shutdown failed with exit code $LASTEXITCODE"
}

Invoke-VhdxCompact -Path $UbuntuVhdxPath

if ($IncludeDocker) {
    Invoke-VhdxCompact -Path $DockerVhdxPath
}

Write-Host ""
Write-Host "Done. Start WSL again by opening Ubuntu or running: wsl"
