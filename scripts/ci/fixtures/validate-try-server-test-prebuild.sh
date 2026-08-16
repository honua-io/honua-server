#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-prebuild-fallback.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT

source_root="${fixture}/source"
policy_root="${fixture}/policy"
tools="${fixture}/tools"
mkdir -p "${source_root}/.github" "${source_root}/tests/Project/bin/Release" \
  "${source_root}/tests/Project/obj" "${source_root}/scripts/ci" \
  "${policy_root}/.github" "${policy_root}/scripts/ci" "${tools}"
touch "${source_root}/tests/Project/Project.csproj"
printf 'stale\n' > "${source_root}/tests/Project/bin/Release/stale.dll"
printf 'stale\n' > "${source_root}/tests/Project/obj/stale.txt"
cat > "${source_root}/.github/server-test-artifact-projects.json" <<'JSON'
{"projects":[{"artifact_suffix":"candidate-controlled","csproj":"tests/Project/Project.csproj"}]}
JSON
cat > "${policy_root}/.github/server-test-artifact-projects.json" <<'JSON'
{"projects":[{"artifact_suffix":"project","csproj":"tests/Project/Project.csproj"}]}
JSON
cat > "${source_root}/scripts/ci/dotnet-restore-retry.sh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf 'restore:%s\n' "$1" >> "${PREBUILD_FIXTURE_LOG}"
SH
chmod +x "${source_root}/scripts/ci/dotnet-restore-retry.sh"
cat > "${tools}/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "--version" ]]; then printf '10.0.fixture\n'; exit 0; fi
printf 'dotnet:%s\n' "$*" >> "${PREBUILD_FIXTURE_LOG}"
SH
chmod +x "${tools}/dotnet"
cat > "${policy_root}/scripts/ci/server-test-prebuild-receipt.py" <<'PY'
#!/usr/bin/env python3
import os, sys
sys.exit(int(os.environ.get("PREBUILD_RECEIPT_EXIT", "0")))
PY
cat > "${policy_root}/scripts/ci/restore-server-test-binaries.sh" <<'SH'
#!/usr/bin/env bash
exit "${PREBUILD_RESTORE_EXIT:-0}"
SH
chmod +x "${policy_root}/scripts/ci/restore-server-test-binaries.sh"

export PATH="${tools}:${PATH}"
export PREBUILD_FIXTURE_LOG="${fixture}/commands.log"
sha="$(printf 'a%.0s' {1..40})"
common=(
  --source-root "${source_root}" --policy-root "${policy_root}"
  --repository honua/example --pull-request 42 --source-sha "${sha}"
  --policy-sha "${sha}" --producer-policy-sha "${sha}" \
  --project tests/Project/Project.csproj
  --producer-run-id 99 --producer-run-attempt 1 --runner-image ubuntu-fixture
)

run_case() {
  local name="$1" payload="$2"
  rm -f "${PREBUILD_FIXTURE_LOG}"
  "${repo_root}/scripts/ci/try-server-test-prebuild.sh" "${common[@]}" \
    --payload "${payload}" --github-output "${fixture}/${name}.out" \
    --metrics "${fixture}/${name}.json"
}

run_case missing "${fixture}/missing"
grep -Fxq 'mode=local-fallback' "${fixture}/missing.out"
grep -Fxq 'reason=artifact-unavailable' "${fixture}/missing.out"
grep -Fq 'restore:tests/Project/Project.csproj' "${PREBUILD_FIXTURE_LOG}"
test ! -e "${source_root}/tests/Project/bin/Release/stale.dll"
test ! -e "${source_root}/tests/Project/obj/stale.txt"

payload="${fixture}/payload"
mkdir -p "${payload}"
touch "${payload}/server-test-binaries-project.manifest.json" \
  "${payload}/server-test-binaries-project.tar.gz" \
  "${payload}/server-test-prebuild-project.receipt.json"

export PREBUILD_RECEIPT_EXIT=1 PREBUILD_RESTORE_EXIT=0
run_case receipt "${payload}"
grep -Fxq 'reason=receipt-rejected' "${fixture}/receipt.out"
grep -Fq 'dotnet:build' "${PREBUILD_FIXTURE_LOG}"

export PREBUILD_RECEIPT_EXIT=0 PREBUILD_RESTORE_EXIT=1
run_case restore "${payload}"
grep -Fxq 'reason=restore-rejected' "${fixture}/restore.out"
grep -Fq 'dotnet:build' "${PREBUILD_FIXTURE_LOG}"

export PREBUILD_RECEIPT_EXIT=0 PREBUILD_RESTORE_EXIT=0
run_case accepted "${payload}"
grep -Fxq 'mode=prebuild' "${fixture}/accepted.out"
grep -Fxq 'reason=accepted' "${fixture}/accepted.out"
test ! -s "${PREBUILD_FIXTURE_LOG}"

echo 'try-server-test-prebuild=ok misses-fallback-immediately'
