#!/usr/bin/env bash
set -euo pipefail
# rc.1 cannot use the file-secret launcher. This private rc.2 fixture keeps that
# launcher while retaining the older schema needed to exercise verified restore.
source=5209c952f5207e9aee6483c53f8ccb86e6a7682a
context=$(mktemp -d)
cleanup() { rm -rf "${context:?}"; }
trap cleanup EXIT
git cat-file -e "$source^{commit}" 2>/dev/null || git fetch --depth=1 origin "$source"
git archive "$source" | tar -x -C "$context"
python3 scripts/docker/prepare_lifecycle_prior.py "$context"
docker build --file "$context/Dockerfile" --tag honua-lifecycle:prior \
  --build-arg HONUA_STORE_PROFILE=postgis --build-arg "HONUA_GIT_SHA=$source" \
  --label org.honua.test-only=true --label org.honua.max-schema=140 \
  --secret id=github_actor,env=GITHUB_ACTOR --secret id=github_token,env=GITHUB_TOKEN \
  "$context"
