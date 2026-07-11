#!/usr/bin/env bash
# Fast fixture validation for the server-test binary artifact contract.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
SHARDS="${REPO_ROOT}/.github/ci-shards.json"

echo "Validating server-test artifact project registry..."
jq -e '
  .contract_version == 1 and
  (.projects | length == 10) and
  (all(.projects[];
    (.artifact_suffix | type == "string" and test("^[a-z0-9-]+$")) and
    (.csproj | type == "string" and endswith(".csproj")) and
    (.proof_filter | type == "string" and length > 0))) and
  (([.projects[].artifact_suffix] | length) == ([.projects[].artifact_suffix] | unique | length)) and
  (([.projects[].csproj] | length) == ([.projects[].csproj] | unique | length))
' "${REGISTRY}" >/dev/null

registered="$(jq -c '[.projects[].csproj] | sort' "${REGISTRY}")"
owned="$(jq -c '[.shards[] | if ((.csproj // "") == "") then "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" else .csproj end] | unique | sort' "${SHARDS}")"
if [[ "${registered}" != "${owned}" ]]; then
  echo "::error::Artifact project registry must exactly equal the unique ci-shards project set." >&2
  diff -u <(jq -r '.[]' <<<"${owned}") <(jq -r '.[]' <<<"${registered}") || true
  exit 1
fi
while IFS= read -r project; do
  [[ -f "${REPO_ROOT}/${project}" ]] || { echo "::error::Registered project does not exist: ${project}" >&2; exit 1; }
done < <(jq -r '.projects[].csproj' "${REGISTRY}")

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-artifact-fixture.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT
fixture_repo="${fixture}/repo"
fixture_output="${fixture}/output"
fixture_restore="${fixture}/restore"
project="tests/dotnet/Fixture.Tests/Fixture.Tests.csproj"
mkdir -p "${fixture_repo}/.github" "${fixture_repo}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/"{linux,linux-x64,unix,win-x64,osx-x64}"/native" \
  "${fixture_repo}/tests/dotnet/Fixture.Tests/obj" "${fixture_output}" "${fixture_restore}"
printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "${fixture_repo}/${project}"
printf '{"contract_version":1,"projects":[{"artifact_suffix":"fixture","csproj":"%s","proof_filter":"Category=Unit"}]}\n' "${project}" \
  > "${fixture_repo}/.github/server-test-artifact-projects.json"
printf '{}\n' > "${fixture_repo}/tests/dotnet/Fixture.Tests/obj/project.assets.json"
for file in Fixture.Tests.dll Fixture.Tests.pdb Fixture.Tests.deps.json Fixture.Tests.runtimeconfig.json testhost.dll; do
  printf 'fixture-%s\n' "${file}" > "${fixture_repo}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/${file}"
done
for runtime in linux linux-x64 unix win-x64 osx-x64; do
  printf '%s\n' "${runtime}" > "${fixture_repo}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/${runtime}/native/runtime.bin"
done

source_sha="0123456789abcdef0123456789abcdef01234567"
HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT="${fixture_repo}" \
HONUA_SERVER_TEST_ARTIFACT_REGISTRY="${fixture_repo}/.github/server-test-artifact-projects.json" \
HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
  "${SCRIPT_DIR}/package-server-test-binaries.sh" \
    --project "${project}" --output "${fixture_output}" --source-sha "${source_sha}"
manifest="${fixture_output}/server-test-binaries-fixture.manifest.json"
archive="${fixture_output}/server-test-binaries-fixture.tar.gz"

jq -e --arg project "${project}" --arg source_sha "${source_sha}" '
  .contract == "honua.server-test-binaries.v1" and
  .project == $project and .source_sha == $source_sha and
  .raw_bytes > .unpacked_bytes and .unpacked_bytes > 0 and .archive_bytes > 0 and
  .file_count >= 9 and .package_milliseconds >= 0
' "${manifest}" >/dev/null
listing="$(tar -tzf "${archive}")"
grep -q '/runtimes/linux-x64/' <<<"${listing}"
grep -q '/runtimes/linux/' <<<"${listing}"
grep -q '/runtimes/unix/' <<<"${listing}"
grep -q 'Fixture.Tests.pdb' <<<"${listing}"
if grep -Eq '/runtimes/(win|osx)' <<<"${listing}"; then
  echo "::error::Fixture archive retained a prohibited RID." >&2
  exit 1
fi

HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
  --manifest "${manifest}" --destination "${fixture_restore}" \
  --project "${project}" --source-sha "${source_sha}"
[[ -f "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/Fixture.Tests.dll" ]]
[[ -f "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/linux-x64/native/runtime.bin" ]]
[[ ! -e "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/win-x64" ]]

cp "${archive}" "${archive}.valid"
printf 'tamper\n' >> "${archive}"
if HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${fixture}/tampered" \
    --project "${project}" --source-sha "${source_sha}" >/dev/null 2>&1; then
  echo "::error::Tampered artifact was accepted." >&2
  exit 1
fi
mv "${archive}.valid" "${archive}"

if HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES=1 \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${fixture}/oversize" \
    --project "${project}" --source-sha "${source_sha}" >/dev/null 2>&1; then
  echo "::error::Oversized artifact was accepted." >&2
  exit 1
fi

echo "Server-test binary artifact contract validation passed (10 projects + fixture integrity)."
