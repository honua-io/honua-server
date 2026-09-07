# Provision once from the deployment's persistent-volume and backup inventory.
# This script declares that contract; it does not certify physical durability.
# Every digest-relevant setting is a parameter: the emitted ConfigurationDigest
# only matches a host whose bound section carries these exact values, so a
# topology that tunes sweeping or inlining must provision with the same tuning.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$StoreReference,
    [Parameter(Mandatory)][ValidateSet('shared-persistent')][string]$PersistenceClass,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$')][string]$BackupIdentity,
    [Parameter(Mandatory)][string[]]$BackupStoreReferences,
    [ValidatePattern('^(?!.*\.\.)[A-Za-z0-9][A-Za-z0-9._/-]{0,158}$')][string]$KeyPrefix = 'gp/outputs',
    [ValidateRange(1024, 8388608)][int]$MaxInlineArtifactBytes = 4194304,
    [TimeSpan]$ReadLeaseDuration = '00:15:00',
    [TimeSpan]$SweepInterval = '00:15:00',
    [TimeSpan]$SweepGrace = '01:00:00',
    [TimeSpan]$OrphanRetention = '7.00:00:00'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($RootPath)) {
    throw 'Mount the persistent volume at an absolute existing root before provisioning.'
}
# Windows PowerShell 5.1 lacks IsPathFullyQualified. Resolve rooted drive-relative
# and root-relative inputs before emitting a runtime configuration.
$RootPath = [IO.Path]::GetFullPath($RootPath)
if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
    throw 'Mount the persistent volume at an absolute existing root before provisioning.'
}
# ValidateSet accepts casing variants; the versioned runtime contract is lowercase.
$PersistenceClass = 'shared-persistent'
foreach ($duration in @($ReadLeaseDuration, $SweepInterval, $SweepGrace, $OrphanRetention)) {
    if ($duration.Ticks -le 0) { throw 'Lease, sweep and retention durations must be positive.' }
}
if ($ReadLeaseDuration -gt [TimeSpan]::FromDays(1) -or $SweepInterval -gt [TimeSpan]::FromDays(1)) {
    throw 'ReadLeaseDuration and SweepInterval must be no more than one day.'
}
foreach ($reference in $BackupStoreReferences) {
    if ($reference -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,159}\z') { throw 'Backup store references must be opaque identifiers.' }
}
if ($BackupStoreReferences -cnotcontains $StoreReference) { throw 'The staging store is outside the declared backup set.' }
$inventory = [string[]]$BackupStoreReferences.Clone()
[Array]::Sort($inventory, [StringComparer]::Ordinal)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$canonical = @(
    'honua-gp-store-v1', 'local', $StoreReference, $PersistenceClass, $BackupIdentity,
    ($inventory -join ','), $KeyPrefix,
    $MaxInlineArtifactBytes.ToString($invariant),
    $ReadLeaseDuration.Ticks.ToString($invariant), $SweepInterval.Ticks.ToString($invariant),
    $SweepGrace.Ticks.ToString($invariant), $OrphanRetention.Ticks.ToString($invariant)
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
    KeyPrefix = $KeyPrefix; MaxInlineArtifactBytes = $MaxInlineArtifactBytes
    ReadLeaseDuration = $ReadLeaseDuration.ToString(); SweepInterval = $SweepInterval.ToString()
    SweepGrace = $SweepGrace.ToString(); OrphanRetention = $OrphanRetention.ToString()
} } } | ConvertTo-Json -Depth 4
