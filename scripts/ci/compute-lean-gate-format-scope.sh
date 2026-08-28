#!/usr/bin/env bash
# Decide whether the shared lean gate (./.github/actions/lean-gate) runs
# `dotnet format --verify-no-changes` over the whole solution or only over the
# files this change touches (via `--include`), and emit the include list.
#
# WHY THIS PAYS (measured, run 33126837397 / job 98706893656, --verbosity
# diagnostic): the full-solution format check is 5.39 min p50 — 29% of the
# 18.7 min PR Gate — and its cost is NOT workspace loading. Phase breakdown:
#   workspace load                 7%
#   determining formattable files  3%
#   whitespace formatters          5%
#   Code Style analysis           35%
#   Analyzer Reference analysis   49%
# 84% of the cost is running code-style + analyzer analysis over all 6586
# files in every project ("Formatted 0 of 6586 files"). `--include` restricts
# that per-document analysis to the changed files, which is where the time goes.
#
# SAFETY NET / MISS-RISK CONTRACT (same shape as the affected build scope):
# a format/analyzer diagnostic triggered in a file OUTSIDE the diff — e.g. a
# deleted type leaving an unnecessary using elsewhere — is not caught by the
# scoped per-PR run. It IS caught by the full-solution `dotnet format` that the
# merge train / ci.yml `Build & Format Check` runs before the change lands, and
# is fixed forward from there; it never reaches trunk unverified.
#
# WHEN THIS REFUSES TO NARROW (falls back to `full`):
#   * The caller asked for `full` (default), or the event is not pull_request
#     (no trustworthy diff base), or the base commit is not in the checkout.
#   * compute-affected-projects.sh reports ALL — the SAME shared force-full
#     decision the affected build scope uses (Directory.*.props, Honua.sln,
#     global.json, NuGet.config, .github/, scripts/ci/, src/Honua.Analyzers/).
#     A Directory.Packages.props edit can change analyzer behavior everywhere,
#     so it must widen the format check exactly as it widens the build. Do not
#     re-derive that list here; the shared script owns it.
#   * The diff touches format-sensitive configuration the shared list cannot
#     see: any .editorconfig, .globalconfig, .csproj, nested .props/.targets,
#     or .sln/.slnf/.ruleset. Those change what "formatted" MEANS for files
#     outside the diff, so `--include`-ing only the diff would under-check.
#   * The include list would exceed GATE_FORMAT_MAX_FILES (default 200).
#     `--include` takes the paths on the command line; rather than risk
#     ARG_MAX or SILENTLY TRUNCATING the list (a quiet coverage hole), a huge
#     diff simply pays for the full run it would approximate anyway.
#
# Deleted/renamed-away files are filtered out (dotnet format errors on paths
# that do not exist). A diff with NO surviving formattable files emits
# mode=skip: no .cs changed and no format-sensitive config changed, so a
# scoped run would verify nothing — the residual cross-file risk is the same
# miss-risk contract above.
#
# Inputs (env):
#   FORMAT_SCOPE           'full' (default) or 'affected'.
#   GITHUB_EVENT_NAME      Event driving the caller; only 'pull_request' has a
#                          trustworthy diff base.
#   GATE_BASE_REF          Optional explicit base ref (tests use this).
#                          Defaults to HEAD^1 — on a pull_request the
#                          checked-out ref is refs/pull/N/merge, whose first
#                          parent IS the current base branch tip. Requires
#                          fetch-depth >= 2.
#   GATE_FORMAT_MAX_FILES  Include-list bound before falling back to full
#                          (default 200).
#   GATE_FORMAT_INCLUDE_PATH  Where to write the include list, one path per
#                          line (default "${RUNNER_TEMP}/lean-gate-format-include.txt").
#
# Outputs:
#   Appends `mode=` (full|affected|skip) and `include-file=` (path, empty
#   unless affected) to $GITHUB_OUTPUT when set, and always prints a
#   human-readable summary naming which mode ran and why.
#
# Exit codes: 0 always, unless invoked outside a git repository.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

format_scope="${FORMAT_SCOPE:-full}"
event_name="${GITHUB_EVENT_NAME:-}"
max_files="${GATE_FORMAT_MAX_FILES:-200}"
include_path="${GATE_FORMAT_INCLUDE_PATH:-${RUNNER_TEMP:-/tmp}/lean-gate-format-include.txt}"

emit() {
  local mode="$1" include_file="$2" reason="$3"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo "mode=${mode}"
      echo "include-file=${include_file}"
    } >> "${GITHUB_OUTPUT}"
  fi
  echo "::notice::Lean gate format scope: ${mode} (${reason})"
}

full() {
  emit "full" "" "$1"
  exit 0
}

if [[ "${format_scope}" != "affected" ]]; then
  full "caller requested format-scope=${format_scope}"
fi

# Only a pull_request has a merge ref whose first parent is the base branch;
# merge_group batches and dispatch/schedule events stay solution-wide.
base_ref="${GATE_BASE_REF:-}"
if [[ -z "${base_ref}" ]]; then
  if [[ "${event_name}" != "pull_request" ]]; then
    full "event '${event_name:-unknown}' has no trustworthy diff base"
  fi
  base_ref="HEAD^1"
fi

if ! git cat-file -e "${base_ref}^{commit}" 2>/dev/null; then
  full "base ref '${base_ref}' is not present in this checkout (fetch-depth too shallow)"
fi

# Reuse the SHARED force-full decision. compute-affected-projects.sh emits the
# single token ALL when the diff touches the props/sln/CI/analyzer paths that
# invalidate any narrowing; those paths change analyzer inputs everywhere, so
# the format check must widen exactly when the build does.
affected=""
if ! affected="$(BASE_REF="${base_ref}" HEAD_REF="HEAD" scripts/ci/compute-affected-projects.sh 2>&1)"; then
  full "compute-affected-projects.sh failed; refusing to narrow the format check"
fi
if printf '%s\n' "${affected}" | grep -qx 'ALL'; then
  full "shared build infrastructure changed (compute-affected-projects.sh reported ALL)"
fi

changed_files="$(git diff --name-only "${base_ref}...HEAD" 2>/dev/null || true)"
if [[ -z "${changed_files}" ]]; then
  full "empty diff against '${base_ref}'; nothing trustworthy to scope by"
fi

# Format-sensitive configuration the shared force-full list cannot see: these
# files change what "correctly formatted" means for files OUTSIDE the diff, so
# an --include of the diff alone would under-check.
if printf '%s\n' "${changed_files}" \
  | grep -qE '((^|/)\.editorconfig$|\.globalconfig$|\.csproj$|\.props$|\.targets$|\.slnf?$|\.ruleset$)'; then
  full "diff touches format-sensitive configuration (.editorconfig/.globalconfig/.csproj/.props/.targets/.sln/.ruleset)"
fi

# The documents dotnet format actually analyses in this workspace, still
# present on disk (a deleted or renamed-away path makes `--include` error).
declare -a include_files=()
while IFS= read -r file; do
  [[ -z "${file}" ]] && continue
  case "${file}" in
    *.cs|*.vb) ;;
    *) continue ;;
  esac
  [[ -f "${file}" ]] || continue
  include_files+=("${file}")
done <<< "${changed_files}"

if [[ ${#include_files[@]} -eq 0 ]]; then
  emit "skip" "" "diff contains no surviving formattable files and no format-sensitive configuration"
  exit 0
fi

# Bounded, never truncated: a silently shortened include list is a silent
# coverage hole. Past the bound, run the full check the list would approximate.
if [[ ${#include_files[@]} -gt ${max_files} ]]; then
  full "${#include_files[@]} changed formattable files exceed the ${max_files}-file include bound"
fi

mkdir -p "$(dirname "${include_path}")"
printf '%s\n' "${include_files[@]}" > "${include_path}"

echo "Lean gate format include list (${#include_files[@]} files):"
printf '  %s\n' "${include_files[@]}"
emit "affected" "${include_path}" "${#include_files[@]} changed formattable files"
