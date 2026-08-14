#!/usr/bin/env bash
# Prove transfer benchmarks execute project commands in the selected checkout.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-benchmark-root.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT

source_root="${fixture}/source"
outside_root="${fixture}/outside"
fake_bin="${fixture}/bin"
mkdir -p "${source_root}/.github" "${outside_root}" "${fake_bin}"
cp "${REPO_ROOT}/.github/server-test-artifact-projects.json" "${source_root}/.github/"
project="$(jq -er '.projects[0].csproj' "${source_root}/.github/server-test-artifact-projects.json")"
mkdir -p "${source_root}/$(dirname "${project}")"
: > "${source_root}/${project}"

cat > "${fake_bin}/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "${PWD}" >> "${DOTNET_PWD_LOG}"
if [[ "${1:-}" == "--version" ]]; then
  echo '10.0.100'
  exit 0
fi
if [[ " $* " == *' --list-tests '* ]]; then
  exit 0
fi
results=''
name='result.trx'
while [[ $# -gt 0 ]]; do
  case "$1" in
    --results-directory) results="$2"; shift 2 ;;
    --logger)
      if [[ "$2" == trx\;LogFileName=* ]]; then name="${2#trx;LogFileName=}"; fi
      shift 2 ;;
    *) shift ;;
  esac
done
mkdir -p "${results}"
printf '%s\n' '<TestRun><Results><UnitTestResult testName="Fixture" outcome="Passed" /></Results></TestRun>' \
  > "${results}/${name}"
SH
chmod +x "${fake_bin}/dotnet"

(
  cd "${outside_root}"
  PATH="${fake_bin}:${PATH}" DOTNET_PWD_LOG="${fixture}/dotnet-pwds" \
    HONUA_SERVER_TEST_BENCHMARK_REPO_ROOT="${source_root}" \
    "${SCRIPT_DIR}/benchmark-server-test-transfer.sh" consumer-ready \
      --project "${project}" --source-sha "$(printf 'a%.0s' {1..40})" \
      --metrics metrics/result.json --identity fixture --filter Fixture \
      --job-start-epoch-ms 1 >/dev/null
)

test -f "${outside_root}/metrics/result.json"
test ! -e "${source_root}/metrics"
while IFS= read -r invoked_from; do
  test "${invoked_from}" = "${source_root}"
done < "${fixture}/dotnet-pwds"

echo 'benchmark-repo-root=ok'
