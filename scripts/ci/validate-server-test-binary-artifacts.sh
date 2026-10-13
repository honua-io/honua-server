#!/usr/bin/env bash
# Fast fixture validation for the server-test binary artifact contract.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# CR-safe jq: strip the CRLF the Windows jq binary emits in text mode so
# captured values, path checks, and comparisons below stay clean (no-op on Linux).
source "${SCRIPT_DIR}/lib/jq-cr-safe.sh"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
SHARDS="${REPO_ROOT}/.github/ci-shards.json"

echo "Validating server-test artifact project registry..."
jq -e '
  .contract_version == 1 and
  (.projects | type == "array" and length > 0) and
  (all(.projects[];
    (.artifact_suffix | type == "string" and test("^[a-z0-9-]+$")) and
    (.csproj | type == "string" and endswith(".csproj")) and
    (.proof_filter | type == "string" and length > 0))) and
  (([.projects[].artifact_suffix] | length) == ([.projects[].artifact_suffix] | unique | length)) and
  (([.projects[].csproj] | length) == ([.projects[].csproj] | unique | length))
' "${REGISTRY}" >/dev/null

registered="$(jq -c '[.projects[].csproj] | sort' "${REGISTRY}")"
owned="$(python3 "${SCRIPT_DIR}/managed-test-build.py" inventory | jq -c 'sort')"
if [[ "${registered}" != "${owned}" ]]; then
  echo "::error::Artifact project registry must exactly equal the scheduled managed-test consumer set." >&2
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
HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1000 \
  "${SCRIPT_DIR}/package-server-test-binaries.sh" \
    --project "${project}" --output "${fixture_output}" --source-sha "${source_sha}"
manifest="${fixture_output}/server-test-binaries-fixture.manifest.json"
archive="${fixture_output}/server-test-binaries-fixture.tar.gz"

jq -e --arg project "${project}" --arg source_sha "${source_sha}" '
  .contract == "honua.server-test-binaries.v1" and
  .project == $project and .source_sha == $source_sha and
  .raw_bytes > .unpacked_bytes and .unpacked_bytes > 0 and .archive_bytes > 0 and
  .file_count >= 9 and .package_milliseconds >= 0 and
  .created_at_epoch == 1000 and .expires_at_epoch == 87400
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

HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1001 \
HONUA_SERVER_TEST_ARTIFACT_TIMING_FILE="${fixture}/restore-timing.json" \
HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
  --manifest "${manifest}" --destination "${fixture_restore}" \
  --project "${project}" --source-sha "${source_sha}"
[[ -f "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/Fixture.Tests.dll" ]]
[[ -f "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/linux-x64/native/runtime.bin" ]]
[[ ! -e "${fixture_restore}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/runtimes/win-x64" ]]
jq -e '.integrity_check_ms >= 0 and .unpack_ms >= 0' "${fixture}/restore-timing.json" >/dev/null

cp "${archive}" "${archive}.valid"
printf 'tamper\n' >> "${archive}"
if HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1001 \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${fixture}/tampered" \
    --project "${project}" --source-sha "${source_sha}" >/dev/null 2>&1; then
  echo "::error::Tampered artifact was accepted." >&2
  exit 1
fi
mv "${archive}.valid" "${archive}"

if HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=87401 \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${fixture}/expired" \
    --project "${project}" --source-sha "${source_sha}" >/dev/null 2>&1; then
  echo "::error::Expired artifact evidence was accepted." >&2
  exit 1
fi

if HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES=1 \
  HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1001 \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${fixture}/oversize" \
    --project "${project}" --source-sha "${source_sha}" >/dev/null 2>&1; then
  echo "::error::Oversized artifact was accepted." >&2
  exit 1
fi

# #4453: a static web asset content root that is another project's build output must
# travel with the payload, and a root no consumer could reproduce must fail loudly on
# one side or the other. A materialized shard must serve what a building shard serves.
echo "Validating static web asset content roots travel with the payload..."
swa="${fixture}/swa"
swa_repo="${swa}/repo"
swa_output="${swa}/output"
swa_nuget="${swa}/nuget"
swa_project="tests/dotnet/Hosted.Tests/Hosted.Tests.csproj"
swa_bin="${swa_repo}/tests/dotnet/Hosted.Tests/bin/Release/net10.0"
mkdir -p "${swa_repo}/.github" "${swa_bin}" "${swa_repo}/tests/dotnet/Hosted.Tests/obj" "${swa_output}" \
  "${swa_repo}/samples/Demo/wwwroot" \
  "${swa_repo}/samples/Demo/bin/Release/net10.0/wwwroot/_framework" \
  "${swa_repo}/samples/Demo/obj/Release/net10.0/compressed/_framework" \
  "${swa_nuget}/demo.auth/1.0.0/staticwebassets"
printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "${swa_repo}/${swa_project}"
printf '{"contract_version":1,"projects":[{"artifact_suffix":"hosted","csproj":"%s","proof_filter":"Category=Unit"}]}\n' "${swa_project}" \
  > "${swa_repo}/.github/server-test-artifact-projects.json"
printf '{}\n' > "${swa_repo}/tests/dotnet/Hosted.Tests/obj/project.assets.json"
for file in Hosted.Tests.dll Hosted.Tests.pdb Hosted.Tests.deps.json Hosted.Tests.runtimeconfig.json; do
  printf 'fixture-%s\n' "${file}" > "${swa_bin}/${file}"
done
printf '<html></html>\n' > "${swa_repo}/samples/Demo/wwwroot/index.html"
printf 'blazor\n' > "${swa_repo}/samples/Demo/bin/Release/net10.0/wwwroot/_framework/blazor.webassembly.js"
printf 'gzip\n' > "${swa_repo}/samples/Demo/obj/Release/net10.0/compressed/_framework/blazor.webassembly.js.gz"
printf 'auth\n' > "${swa_nuget}/demo.auth/1.0.0/staticwebassets/AuthenticationService.js"
printf 'bin/\nobj/\n' > "${swa_repo}/.gitignore"
git -C "${swa_repo}" init -q
git -C "${swa_repo}" add .gitignore samples/Demo/wwwroot/index.html "${swa_project}"

swa_roots=(
  "${swa_nuget}/demo.auth/1.0.0/staticwebassets/"
  "${swa_repo}/samples/Demo/wwwroot/"
  "${swa_repo}/samples/Demo/bin/Release/net10.0/wwwroot/"
  "${swa_repo}/samples/Demo/obj/Release/net10.0/compressed/"
  "${swa_repo}/src/Server/obj/Release/net10.0/compressed/"
  "${swa_bin}/wwwroot/"
)
write_swa_manifest() {
  jq -n '{ContentRoots: $ARGS.positional, Root: {Children: {}}}' --args "$@" \
    > "${swa_bin}/Hosted.staticwebassets.runtime.json"
}
swa_package() {
  HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT="${swa_repo}" \
  HONUA_SERVER_TEST_ARTIFACT_REGISTRY="${swa_repo}/.github/server-test-artifact-projects.json" \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
  HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1000 \
  NUGET_PACKAGES="${swa_nuget}" \
    "${SCRIPT_DIR}/package-server-test-binaries.sh" \
      --project "${swa_project}" --output "${swa_output}" --source-sha "${source_sha}"
}
swa_manifest="${swa_output}/server-test-binaries-hosted.manifest.json"
swa_archive="${swa_output}/server-test-binaries-hosted.tar.gz"
swa_restore() {
  HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1001 \
  HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="fixture-sdk" \
    "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
      --manifest "${swa_manifest}" --destination "$1" \
      --project "${swa_project}" --source-sha "${source_sha}"
}

write_swa_manifest "${swa_roots[@]}"
swa_package >/dev/null
jq -e --arg repo "${swa_repo}" --arg nuget "${swa_nuget}/demo.auth/1.0.0/staticwebassets" '
  .repo_root == $repo and
  .static_web_asset_content_roots == {
    payload: [
      "samples/Demo/bin/Release/net10.0/wwwroot",
      "samples/Demo/obj/Release/net10.0/compressed",
      "src/Server/obj/Release/net10.0/compressed"
    ],
    checkout: ["samples/Demo/wwwroot"],
    nuget: [$nuget]
  }
' "${swa_manifest}" >/dev/null || {
  echo "::error::Static web asset content roots were not classified as payload/checkout/nuget." >&2
  jq '.static_web_asset_content_roots' "${swa_manifest}" >&2
  exit 1
}
swa_listing="$(tar -tzf "${swa_archive}")"
grep -qx './samples/Demo/bin/Release/net10.0/wwwroot/_framework/blazor.webassembly.js' <<<"${swa_listing}"
grep -qx './samples/Demo/obj/Release/net10.0/compressed/_framework/blazor.webassembly.js.gz' <<<"${swa_listing}"
grep -qx './src/Server/obj/Release/net10.0/compressed/' <<<"${swa_listing}"
grep -qx './tests/dotnet/Hosted.Tests/bin/Release/net10.0/wwwroot/' <<<"${swa_listing}"
if grep -Eq 'samples/Demo/wwwroot/index\.html|AuthenticationService\.js' <<<"${swa_listing}"; then
  echo "::error::Payload staged checkout or NuGet content that a consumer already has." >&2
  exit 1
fi

# A clean checkout holds only tracked files; the payload must supply the rest.
swa_consumer="${swa}/consumer"
mkdir -p "${swa_consumer}/samples/Demo/wwwroot"
cp "${swa_repo}/samples/Demo/wwwroot/index.html" "${swa_consumer}/samples/Demo/wwwroot/"
swa_restore "${swa_consumer}" >/dev/null
[[ -f "${swa_consumer}/samples/Demo/bin/Release/net10.0/wwwroot/_framework/blazor.webassembly.js" ]]
[[ -f "${swa_consumer}/samples/Demo/obj/Release/net10.0/compressed/_framework/blazor.webassembly.js.gz" ]]
[[ -d "${swa_consumer}/src/Server/obj/Release/net10.0/compressed" ]]
[[ -d "${swa_consumer}/tests/dotnet/Hosted.Tests/bin/Release/net10.0/wwwroot" ]]

if swa_restore "${swa}/consumer-without-checkout-root" >/dev/null 2>&1; then
  echo "::error::Restore accepted a consumer missing a checkout content root." >&2
  exit 1
fi
mv "${swa_nuget}" "${swa}/nuget-away"
if swa_restore "${swa}/consumer-without-nuget-root" >/dev/null 2>&1; then
  echo "::error::Restore accepted a consumer missing a NuGet content root." >&2
  exit 1
fi
mv "${swa}/nuget-away" "${swa_nuget}"

cp "${swa_manifest}" "${swa_manifest}.valid"
jq '.static_web_asset_content_roots.payload += ["../escape/bin"]' "${swa_manifest}.valid" > "${swa_manifest}"
if swa_restore "${swa}/consumer-unsafe-root" >/dev/null 2>&1; then
  echo "::error::Restore accepted an unsafe declared payload root." >&2
  exit 1
fi
mv "${swa_manifest}.valid" "${swa_manifest}"

printf 'generated\n' > "${swa_repo}/samples/Demo/wwwroot/generated.js"
if swa_package >/dev/null 2>&1; then
  echo "::error::Packaging accepted a checkout content root holding files a clean checkout lacks." >&2
  exit 1
fi
rm "${swa_repo}/samples/Demo/wwwroot/generated.js"

write_swa_manifest "${swa_roots[@]}" "${swa}/elsewhere/wwwroot/"
mkdir -p "${swa}/elsewhere/wwwroot"
if swa_package >/dev/null 2>&1; then
  echo "::error::Packaging accepted a content root outside the repository and NuGet package folder." >&2
  exit 1
fi

project_count="$(jq ' .projects | length ' "${REGISTRY}")"
echo "Server-test binary artifact contract validation passed (${project_count} projects + fixture integrity + static web asset content roots)."

# Same-workflow strict build-once identity, without invoking dotnet.
export HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID=12345
export HONUA_SERVER_TEST_ARTIFACT_BUILD_PROPERTIES='TreatWarningsAsErrors=true;RunAnalyzers=true;UseSharedCompilation=false'
export HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK=10.0.100
export HONUA_SERVER_TEST_ARTIFACT_REGISTRY="${fixture_repo}/.github/server-test-artifact-projects.json"
export HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH=1001
# Earlier cases deliberately alter the fixture; restore the original valid payload.
mkdir -p "${fixture_repo}/tests/dotnet/Fixture.Tests/bin/Release/net10.0"
for file in Fixture.Tests.dll Fixture.Tests.pdb Fixture.Tests.deps.json Fixture.Tests.runtimeconfig.json; do
  printf 'strict-%s\n' "${file}" > "${fixture_repo}/tests/dotnet/Fixture.Tests/bin/Release/net10.0/${file}"
done
# Remove static manifests used by individual negative fixtures; their independent
# missing-root assertions above remain authoritative.
find "${fixture_repo}/tests/dotnet/Fixture.Tests/bin" -name '*.staticwebassets.runtime.json' -delete
HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT="${fixture_repo}" \
  "${SCRIPT_DIR}/package-server-test-binaries.sh" --project "${project}" --output "${fixture}/strict" --source-sha "${source_sha}"
strict_manifest="${fixture}/strict/server-test-binaries-fixture.manifest.json"
strict_restore() {
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" --manifest "${strict_manifest}" \
    --destination "${fixture}/strict-restored" --project "${project}" --source-sha "${source_sha}"
}
strict_restore
cp "${strict_manifest}" "${strict_manifest}.valid"
for field in workflow_run_id build_properties configuration os architecture registry_sha256; do
  jq --arg field "${field}" '.build_identity[$field]="mismatch"' "${strict_manifest}.valid" > "${strict_manifest}"
  if strict_restore >/dev/null 2>&1; then echo "::error::Strict mismatch accepted: ${field}"; exit 1; fi
  [[ "$(cat "${fixture}/strict-restored/tests/dotnet/Fixture.Tests/bin/Release/net10.0/Fixture.Tests.dll")" == strict-Fixture.Tests.dll ]]
done
cp "${strict_manifest}.valid" "${strict_manifest}"
if HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK=invalid strict_restore >/dev/null 2>&1; then
  echo '::error::Invalid SDK accepted'; exit 1
fi
if HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID=0 strict_restore >/dev/null 2>&1; then
  echo '::error::Invalid run ID accepted'; exit 1
fi
# A valid archive with missing declared static content must not install even its
# otherwise valid binaries, nor replace an existing destination sentinel.
jq '.static_web_asset_content_roots.payload += ["src/Missing/bin/Release"]' "${strict_manifest}.valid" > "${strict_manifest}"
if strict_restore >/dev/null 2>&1; then echo '::error::Missing static payload accepted'; exit 1; fi
[[ "$(cat "${fixture}/strict-restored/tests/dotnet/Fixture.Tests/bin/Release/net10.0/Fixture.Tests.dll")" == strict-Fixture.Tests.dll ]]
[[ ! -e "${fixture}/strict-restored/src/Missing/bin/Release" ]]
echo 'Strict same-run identity and transactional rejection fixtures passed.'
# Force failure after the first root replacement and prove rollback restores old
# bytes. The second destination is a symlink, which must never be followed.
transaction_stage="${fixture}/transaction-stage"
transaction_destination="${fixture}/transaction-destination"
mkdir -p "${transaction_stage}/a/bin/Release" "${transaction_stage}/a/obj" "${transaction_destination}/a/bin/Release" "${fixture}/foreign"
printf new > "${transaction_stage}/a/bin/Release/proof"
printf old > "${transaction_destination}/a/bin/Release/proof"
ln -s "${fixture}/foreign" "${transaction_destination}/a/obj"
printf '{"static_web_asset_content_roots":{"payload":[]}}' > "${fixture}/transaction.json"
# obj sorts before bin/Release by depth; add an initial shallower root so the
# fixture exercises rollback after a successful replacement, rather than precheck.
mkdir -p "${transaction_stage}/first" "${transaction_destination}/first"
printf new > "${transaction_stage}/first/proof"
printf old > "${transaction_destination}/first/proof"
printf '{"static_web_asset_content_roots":{"payload":["first"]}}' > "${fixture}/transaction.json"
if python3 "${SCRIPT_DIR}/install-server-test-artifact.py" "${transaction_stage}" "${transaction_destination}" \
  "${fixture}/transaction.json" a Release >/dev/null 2>&1; then
  echo '::error::Unsafe destination accepted'; exit 1
fi
[[ "$(cat "${transaction_destination}/first/proof")" == old ]]
[[ "$(cat "${transaction_destination}/a/bin/Release/proof")" == old ]]
[[ -L "${transaction_destination}/a/obj" ]]
echo 'Mid-install failure rollback fixture passed.'
cp "${strict_manifest}.valid" "${strict_manifest}"
if HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK=10.0.101 strict_restore >/dev/null 2>&1; then
  echo '::error::Different SDK accepted'; exit 1
fi
jq '.contract="honua.server-test-binaries.v1"' "${strict_manifest}.valid" > "${strict_manifest}"
if strict_restore >/dev/null 2>&1; then echo '::error::Strict consumer accepted legacy v1'; exit 1; fi
echo 'Strict SDK mismatch and downgrade fixtures passed.'

# Strict Server.Tests also owns the canonical executable boot smoke consumes.
boot_project='tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
boot_test_dir="${fixture_repo}/$(dirname "${boot_project}")"
mkdir -p "${boot_test_dir}/bin/Release/net10.0" "${boot_test_dir}/obj"
printf '<Project />' > "${fixture_repo}/${boot_project}"
cp "${fixture_repo}/tests/dotnet/Fixture.Tests/obj/project.assets.json" "${boot_test_dir}/obj/project.assets.json"
for file in Honua.Server.Tests.dll Honua.Server.Tests.pdb Honua.Server.Tests.deps.json Honua.Server.Tests.runtimeconfig.json; do
  printf 'boot-test-%s\n' "${file}" > "${boot_test_dir}/bin/Release/net10.0/${file}"
done
jq --arg project "${boot_project}" '.projects += [{csproj:$project,artifact_suffix:"boot",proof_filter:"FullyQualifiedName~Fixture"}]' \
  "${fixture_repo}/.github/server-test-artifact-projects.json" > "${fixture}/boot-registry.json"
export HONUA_SERVER_TEST_ARTIFACT_REGISTRY="${fixture}/boot-registry.json"
boot_host="${fixture_repo}/src/Honua.Server/bin/Release/net10.0"
boot_package() {
  HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT="${fixture_repo}" \
    "${SCRIPT_DIR}/package-server-test-binaries.sh" --project "${boot_project}" \
      --output "${fixture}/boot-artifact" --source-sha "${source_sha}"
}
if boot_package >/dev/null 2>&1; then echo '::error::Packaged strict Server.Tests without canonical boot output'; exit 1; fi
mkdir -p "${boot_host}"
printf canonical-server > "${boot_host}/Honua.Server.dll"
printf symbols > "${boot_host}/Honua.Server.pdb"
printf dependency > "${boot_host}/Honua.Dependency.dll"
printf '{"runtimeOptions":{"tfm":"net10.0"}}' > "${boot_host}/Honua.Server.runtimeconfig.json"
printf '{"targets":{"net10.0":{"Honua.Server/1":{"runtime":{"Honua.Server.dll":{},"Honua.Dependency.dll":{}}}}}}' > "${boot_host}/Honua.Server.deps.json"
mkdir -p "${boot_host}/fr" "${fixture_repo}/src/Honua.Server/obj/Release/net10.0/boot-static"
printf localized > "${boot_host}/fr/Honua.Dependency.resources.dll"
printf static-host-content > "${fixture_repo}/src/Honua.Server/obj/Release/net10.0/boot-static/content"
jq '.targets["net10.0"]["Honua.Server/1"].resources = {"lib/net10.0/fr/Honua.Dependency.resources.dll":{"locale":"fr"}}' \
  "${boot_host}/Honua.Server.deps.json" > "${fixture}/boot-deps.json"
cp "${fixture}/boot-deps.json" "${boot_host}/Honua.Server.deps.json"
jq -n --arg root "${fixture_repo}/src/Honua.Server/obj/Release/net10.0/boot-static/" '{ContentRoots:[$root]}' \
  > "${boot_host}/Honua.Server.staticwebassets.runtime.json"
boot_package
boot_manifest="${fixture}/boot-artifact/server-test-binaries-boot.manifest.json"
boot_restore() {
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" --manifest "${boot_manifest}" \
    --destination "${fixture}/boot-restored" --project "${boot_project}" --source-sha "${source_sha}"
}
boot_restore
[[ "$(cat "${fixture}/boot-restored/src/Honua.Server/bin/Release/net10.0/Honua.Server.dll")" == canonical-server ]]
[[ "$(cat "${fixture}/boot-restored/src/Honua.Server/obj/Release/net10.0/boot-static/content")" == static-host-content ]]
cp "${boot_manifest}" "${boot_manifest}.valid"
jq 'del(.required_dependency_output_roots,.host_output_contract)' "${boot_manifest}.valid" > "${boot_manifest}"
if boot_restore >/dev/null 2>&1; then echo '::error::Accepted old strict artifact without boot contract'; exit 1; fi
cp "${boot_manifest}.valid" "${boot_manifest}"
# A freshly digested archive may still be semantically incomplete. Preserve the
# existing installed host and test outputs when its dependency validation fails.
rm "${boot_host}/Honua.Dependency.dll"
boot_package
if boot_restore >/dev/null 2>&1; then echo '::error::Accepted incomplete boot-host dependency graph'; exit 1; fi
[[ "$(cat "${fixture}/boot-restored/src/Honua.Server/bin/Release/net10.0/Honua.Dependency.dll")" == dependency ]]
[[ "$(cat "${fixture}/boot-restored/src/Honua.Server/bin/Release/net10.0/Honua.Server.dll")" == canonical-server ]]
echo 'Strict canonical boot-host packaging, dependency validation and old-contract rejection fixtures passed.'
