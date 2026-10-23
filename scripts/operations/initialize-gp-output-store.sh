#!/usr/bin/env bash
# POSIX sibling of Initialize-GpOutputStore.ps1 for Linux deployments, whose
# persistent volumes are mounted by containers rather than by a Windows host.
# Both scripts emit the same versioned canonical form as
# GeoprocessingOutputStoreAttestation.Create, so a marker written here is
# accepted by a server or worker that binds the same section. Provisioning
# declares the persistence and backup contract; it does not certify physical
# durability. Writes the marker and prints the configuration digest.
set -uo pipefail

root_path=""; store_reference=""; persistence_class=""; backup_identity=""
backup_store_references=""; key_prefix="gp/outputs"; max_inline_artifact_bytes="4194304"
read_lease_duration="00:15:00"; sweep_interval="00:15:00"
sweep_grace="01:00:00"; orphan_retention="7.00:00:00"

fail() { printf '%s\n' "$1" >&2; exit 1; }

# Every option takes a value. A missing operand is reported rather than treated
# as empty: `shift 2` with one argument left shifts nothing under `set -uo`, so a
# trailing bare option would otherwise spin this loop forever and hang a
# provisioning job until an external timeout.
while (( $# > 0 )); do
  option="$1"
  case "${option}" in
    --root-path|--store-reference|--persistence-class|--backup-identity|--backup-store-references|\
    --key-prefix|--max-inline-artifact-bytes|--read-lease-duration|--sweep-interval|--sweep-grace|\
    --orphan-retention) (( $# >= 2 )) || fail "option ${option} requires a value"; value="$2"; shift 2 ;;
    *) fail "unknown argument: ${option}" ;;
  esac
  case "${option}" in
    --root-path) root_path="${value}" ;;
    --store-reference) store_reference="${value}" ;;
    --persistence-class) persistence_class="${value}" ;;
    --backup-identity) backup_identity="${value}" ;;
    --backup-store-references) backup_store_references="${value}" ;;
    --key-prefix) key_prefix="${value}" ;;
    --max-inline-artifact-bytes) max_inline_artifact_bytes="${value}" ;;
    --read-lease-duration) read_lease_duration="${value}" ;;
    --sweep-interval) sweep_interval="${value}" ;;
    --sweep-grace) sweep_grace="${value}" ;;
    --orphan-retention) orphan_retention="${value}" ;;
  esac
done

is_identifier() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$ ]]; }

# .NET TimeSpan "[d.]hh:mm:ss" to ticks. Fractional seconds are rejected rather
# than truncated: a silently rounded tick count would produce a digest that no
# host can match, which reads as a mount failure instead of a typo. The day
# component is bounded below TimeSpan.MaxValue's 10675199 days before it is
# multiplied, so a huge input is refused instead of wrapping signed 64-bit Bash
# arithmetic into a small positive tick count that survives the checks below.
timespan_ticks() {
  local value="$1" days=0 rest="$1"
  if [[ "$value" =~ ^([0-9]{1,8})\.([0-9]{1,2}:.*)$ ]]; then
    days="${BASH_REMATCH[1]}"; rest="${BASH_REMATCH[2]}"
    (( 10#$days <= 10675198 )) || return 1
  elif [[ "$value" =~ ^[0-9]+\. ]]; then
    return 1
  fi
  [[ "$rest" =~ ^([0-9]{1,2}):([0-5][0-9]):([0-5][0-9])$ ]] || return 1
  local hours="${BASH_REMATCH[1]#0}" minutes="${BASH_REMATCH[2]#0}" seconds="${BASH_REMATCH[3]#0}"
  (( 10#$hours < 24 )) || return 1
  printf '%s' "$(( ((10#$days * 86400) + (10#${hours:-0} * 3600) + (10#${minutes:-0} * 60) + 10#${seconds:-0}) * 10000000 ))"
}

[[ "$root_path" == /* ]] || fail 'Mount the persistent volume at an absolute existing root before provisioning.'
[[ -d "$root_path" ]] || fail 'Mount the persistent volume at an absolute existing root before provisioning.'
root_path="$(cd "$root_path" && pwd -P)"
is_identifier "$store_reference" || fail 'StoreReference must be an opaque identifier.'
is_identifier "$backup_identity" || fail 'BackupIdentity must be an opaque identifier.'
[[ "$persistence_class" == shared-persistent ]] || fail 'Enabled staging requires the shared-persistent persistence class.'
[[ "$key_prefix" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,158}$ && "$key_prefix" != *".."* ]] \
  || fail 'KeyPrefix must be a bounded relative key prefix without traversal segments.'
[[ "$max_inline_artifact_bytes" =~ ^[0-9]+$ ]] \
  && (( max_inline_artifact_bytes >= 1024 && max_inline_artifact_bytes <= 8388608 )) \
  || fail 'MaxInlineArtifactBytes must be between 1 KiB and the 8 MiB contract ceiling.'

declare -a inventory=()
IFS=',' read -r -a inventory <<< "$backup_store_references"
(( ${#inventory[@]} > 0 )) || fail 'Backup store references must be supplied by the deployment inventory.'
for reference in "${inventory[@]}"; do
  is_identifier "$reference" || fail 'Backup store references must be opaque identifiers.'
done
printf '%s\n' "${inventory[@]}" | grep -qxF "$store_reference" || fail 'The staging store is outside the declared backup set.'
# Ordinal sort matches StringComparer.Ordinal in the runtime canonical form.
sorted_inventory="$(printf '%s\n' "${inventory[@]}" | LC_ALL=C sort | paste -sd, -)"

declare -a ticks=()
for duration in "$read_lease_duration" "$sweep_interval" "$sweep_grace" "$orphan_retention"; do
  value="$(timespan_ticks "$duration")" || fail "Duration '${duration}' is not a whole-second [d.]hh:mm:ss TimeSpan within the .NET TimeSpan range."
  (( value > 0 )) || fail 'Lease, sweep and retention durations must be positive.'
  ticks+=("$value")
done
(( ticks[0] <= 864000000000 && ticks[1] <= 864000000000 )) \
  || fail 'ReadLeaseDuration and SweepInterval must be no more than one day.'

canonical="$(printf '%s\n' honua-gp-store-v1 local "$store_reference" "$persistence_class" "$backup_identity" \
  "$sorted_inventory" "$key_prefix" "$max_inline_artifact_bytes" "${ticks[@]}")"
# The command substitution above already stripped the trailing newline printf
# appended, so `canonical` is the exact form the runtime hashes.
digest="$(printf '%s' "${canonical}" | sha256sum | cut -d' ' -f1)"

marker="${root_path}/.honua-gp-store.json"
# noclobber prevents quietly re-attesting a different store or policy in place.
( set -o noclobber; : > "$marker" ) 2>/dev/null \
  || fail 'A store attestation marker already exists at this root; remove it deliberately before re-provisioning.'
printf '{"Provider":"local","StoreReference":"%s","ConfigurationDigest":"%s","PersistenceClass":"%s","BackupIdentity":"%s"}\n' \
  "$store_reference" "$digest" "$persistence_class" "$backup_identity" > "$marker"
printf '%s\n' "$digest"
