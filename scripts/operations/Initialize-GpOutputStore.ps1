# Provision once from the deployment's persistent-volume and backup inventory.
# This script declares that contract; it does not certify physical durability.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$StoreReference,
    [Parameter(Mandatory)][ValidateSet('shared-persistent')][string]$PersistenceClass,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$BackupIdentity,
    [Parameter(Mandatory)][string[]]$BackupStoreReferences,
    [ValidateRange(1024, 8388608)][int]$MaxInlineArtifactBytes = 4194304
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($RootPath) -or -not (Test-Path -LiteralPath $RootPath -PathType Container)) {
    throw 'Mount the persistent volume at an absolute existing root before provisioning.'
}
foreach ($reference in $BackupStoreReferences) {
    if ($reference -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,159}\z') { throw 'Backup store references must be opaque identifiers.' }
}
if ($BackupStoreReferences -cnotcontains $StoreReference) { throw 'The staging store is outside the declared backup set.' }
$inventory = [string[]]$BackupStoreReferences.Clone()
[Array]::Sort($inventory, [StringComparer]::Ordinal)
$canonical = @(
    'honua-gp-store-v1', 'local', $StoreReference, $PersistenceClass, $BackupIdentity,
    ($inventory -join ','), 'gp/outputs',
    $MaxInlineArtifactBytes.ToString([Globalization.CultureInfo]::InvariantCulture),
    '9000000000', '9000000000', '36000000000', '6048000000000'
) -join "`n"
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
$attestation = [ordered]@{
    Provider = 'local'; StoreReference = $StoreReference; ConfigurationDigest = $digest
    PersistenceClass = $PersistenceClass; BackupIdentity = $BackupIdentity
}
$marker = Join-Path $RootPath '.honua-gp-store.json'
# CreateNew prevents quietly re-attesting a different store or policy in place.
$stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($attestation | ConvertTo-Json))
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
} finally { $stream.Dispose() }
# Bind this same section in every server/worker. Only mount paths may differ.
@{ Geoprocessing = @{ OutputStaging = @{
    Enabled = $true; Provider = 'local'; StoreReference = $StoreReference; LocalRootPath = $RootPath
    PersistenceClass = $PersistenceClass; BackupIdentity = $BackupIdentity
    BackupStoreReferences = $inventory; ConfigurationDigest = $digest
    KeyPrefix = 'gp/outputs'; MaxInlineArtifactBytes = $MaxInlineArtifactBytes
    ReadLeaseDuration = '00:15:00'; SweepInterval = '00:15:00'
    SweepGrace = '01:00:00'; OrphanRetention = '7.00:00:00'
} } } | ConvertTo-Json -Depth 4
