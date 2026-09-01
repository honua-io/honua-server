#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
manifest="${SDK_RELEASE_CERTIFICATION_MANIFEST:-$root_dir/docs/developer/sdk-release-certification.json}"
results_dir="${SDK_RELEASE_CERTIFICATION_RESULTS_DIR:-$root_dir/results/sdk-release-certification}"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
mkdir -p "$results_dir"

js_package="$(jq -r '.sdks.js.package' "$manifest")"
js_version="$(jq -r '.sdks.js.version' "$manifest")"
python_package="$(jq -r '.sdks.python.package' "$manifest")"
python_version="$(jq -r '.sdks.python.version' "$manifest")"
dotnet_package="$(jq -r '.sdks.dotnet.package' "$manifest")"
dotnet_version="$(jq -r '.sdks.dotnet.version' "$manifest")"
grpc_package="$(jq -r '.sdks.dotnet.protocolPackage' "$manifest")"
grpc_version="$(jq -r '.sdks.dotnet.protocolVersion' "$manifest")"
dotnet_cmd="$(command -v dotnet || true)"
if [[ -z "$dotnet_cmd" && -x "$HOME/.dotnet/dotnet" ]]; then
  dotnet_cmd="$HOME/.dotnet/dotnet"
fi

jq -n '{js:{installed:false},python:{installed:false},dotnet:{installed:false}}' > "$results_dir/install-results.json"
set +e
mkdir -p "$work_dir/js"
(cd "$work_dir/js" && npm init -y >/dev/null && npm install --registry=https://registry.npmjs.org --ignore-scripts --no-audit --no-fund "$js_package@$js_version") >"$results_dir/js-install.log" 2>&1
js_install=$?
if [[ $js_install -eq 0 ]]; then
  jq '.js={installed:true,registry:"https://registry.npmjs.org"}' "$results_dir/install-results.json" > "$work_dir/install.json" && mv "$work_dir/install.json" "$results_dir/install-results.json"
  cp "$root_dir/scripts/ci/sdk-release-probes/js.mjs" "$work_dir/js/probe.mjs"
  (cd "$work_dir/js" && node probe.mjs "$results_dir/js.json") >"$results_dir/js-probe.log" 2>&1
else
  js_trace="$(tail -n 40 "$results_dir/js-install.log")"
  jq --arg trace "$js_trace" '.js.trace=$trace' "$results_dir/install-results.json" > "$work_dir/install.json" && mv "$work_dir/install.json" "$results_dir/install-results.json"
fi

python_runner=()
if python3 -m venv "$work_dir/venv" >"$results_dir/python-venv.log" 2>&1; then
  "$work_dir/venv/bin/pip" install --index-url https://pypi.org/simple "$python_package==$python_version" >"$results_dir/python-install.log" 2>&1
  python_install=$?
  python_runner=("$work_dir/venv/bin/python")
else
  mkdir -p "$work_dir/python-target"
  python3 -m pip install --target "$work_dir/python-target" --index-url https://pypi.org/simple "$python_package==$python_version" >"$results_dir/python-install.log" 2>&1
  python_install=$?
  python_runner=(env "PYTHONPATH=$work_dir/python-target" python3)
fi
if [[ $python_install -eq 0 ]]; then
  jq '.python={installed:true,registry:"https://pypi.org/simple"}' "$results_dir/install-results.json" > "$work_dir/install.json" && mv "$work_dir/install.json" "$results_dir/install-results.json"
  "${python_runner[@]}" "$root_dir/scripts/ci/sdk-release-probes/python.py" "$results_dir/python.json" >"$results_dir/python-probe.log" 2>&1
else
  python_trace="$(tail -n 40 "$results_dir/python-install.log")"
  jq --arg trace "$python_trace" '.python.trace=$trace' "$results_dir/install-results.json" > "$work_dir/install.json" && mv "$work_dir/install.json" "$results_dir/install-results.json"
fi

mkdir -p "$work_dir/dotnet"
(cd "$work_dir/dotnet" && "$dotnet_cmd" new console --framework net10.0 --no-restore >/dev/null && "$dotnet_cmd" add package "$grpc_package" --version "$grpc_version" --source https://api.nuget.org/v3/index.json >/dev/null && "$dotnet_cmd" restore --source https://api.nuget.org/v3/index.json) >"$results_dir/geospatial-grpc-install.log" 2>&1
grpc_install=$?
(cd "$work_dir/dotnet" && "$dotnet_cmd" add package "$dotnet_package" --version "$dotnet_version" --source https://api.nuget.org/v3/index.json) >"$results_dir/dotnet-install.log" 2>&1
dotnet_install=$?
dotnet_trace="$(tail -n 40 "$results_dir/dotnet-install.log")"
jq --arg trace "$dotnet_trace" --argjson installed "$([[ $dotnet_install -eq 0 ]] && echo true || echo false)" \
  --argjson grpcInstalled "$([[ $grpc_install -eq 0 ]] && echo true || echo false)" \
  '.dotnet={installed:$installed,registry:"https://api.nuget.org/v3/index.json",protocolPackageInstalled:$grpcInstalled,trace:$trace}' \
  "$results_dir/install-results.json" > "$work_dir/install.json" && mv "$work_dir/install.json" "$results_dir/install-results.json"
set -e

python3 "$root_dir/scripts/ci/build-sdk-release-certification.py" --manifest "$manifest" --results-dir "$results_dir" \
  --output "$results_dir/fragment.json" --producer-source-sha "$(git -C "$root_dir" rev-parse HEAD)" \
  --run-url "${GITHUB_SERVER_URL:-local}/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/runs/${GITHUB_RUN_ID:-local}"
jq -e '.operation_scope.complete == true and (.observations | length) == 99' "$results_dir/fragment.json" >/dev/null
jq -e '.passed == true' "$results_dir/report.json" >/dev/null
