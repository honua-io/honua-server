#!/usr/bin/env bash
# Decide whether a pull request's diff touches the CI surface: anything under
# `.github/` (workflows, actions, `ci-shards.json`) or `scripts/ci/`. PR Gate's
# `ci-router-validation` job runs `scripts/ci/validate-ci-router.sh` and
# `scripts/ci/validate-single-merge-authority.sh` only when this answers true;
# ci.yml's trunk-only `ci-router-validation` job already runs them
# unconditionally after merge, so a false positive here costs a few offline
# seconds, never a wrong skip. Any doubt therefore answers true.
#
# USAGE
#   pr-touches-ci-surface.sh [--github-output]            pull_request merge ref (CI)
#   pr-touches-ci-surface.sh --paths < changed-paths.txt  what-if, one path per line
#
# EXIT STATUS
#   0 touches the CI surface, 1 does not, 2 usage error. Every run prints
#   `ci_surface=<true|false>` and `reason=<why>`; with --github-output both
#   lines are also appended to $GITHUB_OUTPUT and the exit status is 0 either
#   way (the caller branches on the output, not the exit code).

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

usage() {
  echo "usage: $(basename "$0") [--github-output] [--paths]" >&2
  exit 2
}

github_output=0
mode=merge-ref
while [[ $# -gt 0 ]]; do
  case "$1" in
    --github-output) github_output=1 ;;
    --paths) mode=paths ;;
    *) usage ;;
  esac
  shift
done

tmp="$(mktemp)"
trap 'rm -f "${tmp}"' EXIT

answer() {
  local verdict="$1"
  local reason
  reason="$(printf '%s' "$2" | tr '\n\r' '  ')"
  echo "ci_surface=${verdict}"
  echo "reason=${reason}"
  if [[ "${github_output}" == 1 ]]; then
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
      printf 'ci_surface=%s\nreason=%s\n' "${verdict}" "${reason}" >> "${GITHUB_OUTPUT}"
    fi
    # --github-output callers (PR Gate's `Classify CI-surface diff` step)
    # branch on the ci_surface output, not the exit code, per the EXIT STATUS
    # contract above — exiting nonzero here instead fails that step (and the
    # whole job) for every PR that does not touch the CI surface, which is
    # every PR except the rare `.github/` / `scripts/ci/` change.
    exit 0
  fi
  if [[ "${verdict}" == true ]]; then
    exit 0
  fi
  exit 1
}

# An unexpected failure anywhere below must answer "touches the CI surface"
# rather than fail the calling job or silently skip the validation.
trap 'trap - ERR; answer true "classifier error at line ${LINENO}; running the check"' ERR

matches_ci_surface() {
  case "$1" in
    .github/*|scripts/ci/*) return 0 ;;
    *) return 1 ;;
  esac
}

if [[ "${mode}" == paths ]]; then
  cat > "${tmp}"
else
  if [[ "${GITHUB_EVENT_NAME:-}" != pull_request ]]; then
    answer true "event '${GITHUB_EVENT_NAME:-unknown}' has no pull-request diff base; running the check"
  fi
  if ! git rev-parse -q --verify 'HEAD^2^{commit}' >/dev/null; then
    answer true "HEAD is not a pull-request merge commit (no second parent); running the check"
  fi
  if ! git cat-file -e 'HEAD^1^{commit}' 2>/dev/null; then
    answer true "the target-branch parent is not in this checkout (fetch-depth too shallow); running the check"
  fi
  if ! git diff --no-renames --name-only 'HEAD^1' HEAD -- > "${tmp}"; then
    answer true "git diff HEAD^1 HEAD failed; running the check"
  fi
fi

while IFS= read -r path; do
  [[ -n "${path}" ]] || continue
  if matches_ci_surface "${path}"; then
    answer true "changed path '${path}' is under the CI surface"
  fi
done < "${tmp}"

answer false "no changed path is under .github/ or scripts/ci/"
