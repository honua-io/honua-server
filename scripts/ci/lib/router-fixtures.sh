#!/usr/bin/env bash
# Bounded, failure-collecting runner. Each fixture inherits its own shell snapshot.
router_fixtures_init() {
  ROUTER_WORKERS="${HONUA_ROUTER_WORKERS:-4}"
  if [[ ! "$ROUTER_WORKERS" =~ ^[1-8]$ ]]; then
    echo 'HONUA_ROUTER_WORKERS must be between 1 and 8.' >&2
    exit 2
  fi
  local changed=false
  case "$#:${1:-}" in
    0:) ;;
    1:--changed-only) changed=true ;;
    *) echo 'Usage: validate-ci-router.sh [--changed-only]' >&2; exit 2 ;;
  esac
  # Give each background worker an owned process group for cancellation.
  set -m
  ROUTER_RESULTS="$(mktemp -d)"
  ROUTER_COUNT=0
  ROUTER_STARTED=$SECONDS
  ROUTER_CHANGED_INPUTS=()
  trap 'router_fixtures_cleanup' EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  # CI always exercises the complete authority, including manual invocations.
  if [[ "$changed" == true && -z "${CI:-}" && -z "${GITHUB_ACTIONS:-}" && -n "$PYTHON_BIN" ]]; then
    "$PYTHON_BIN" "$SCRIPT_DIR/router-fixture-inputs.py" > "$ROUTER_RESULTS/changed"
    mapfile -t ROUTER_CHANGED_INPUTS < "$ROUTER_RESULTS/changed"
    echo "Router changed-only: ${#ROUTER_CHANGED_INPUTS[@]} changed paths (including working tree)."
  else
    ROUTER_CHANGED_INPUTS=('*')
    echo "Router full validation ($ROUTER_WORKERS workers)."
  fi
}

router_fixtures_cleanup() {
  local pid
  # jobs reports only children still owned by this shell, avoiding PID reuse.
  for pid in $(jobs -pr); do kill -TERM -- "-$pid" 2>/dev/null || true; done
  wait || true
  rm -rf -- "${ROUTER_RESULTS:?}"
}

router_fixture_selected() {
  local name="$1" path
  # These stdin-only assertions read exactly the router, jq wrapper and shard map.
  # Other fixtures deliberately run until their complete input closure is declared.
  if [[ "$name" != routing:* ]]; then return 0; fi
  for path in "${ROUTER_CHANGED_INPUTS[@]}"; do
    case "$path" in
      '*'|.github/ci-shards.json|scripts/ci/honua-server-targeted-tests.sh|scripts/ci/lib/jq-cr-safe.sh|scripts/ci/validate-ci-router.sh|scripts/ci/lib/router-fixtures.sh|scripts/ci/router-fixture-inputs.py)
        return 0 ;;
    esac
  done
  return 1
}

run_fixture() {
  local name="$1"
  shift
  if ! router_fixture_selected "$name"; then
    echo "fixture SKIP $name (inputs unchanged)"
    return
  fi
  while (( $(jobs -pr | wc -l) >= ROUTER_WORKERS )); do wait -n || true; done
  ROUTER_COUNT=$((ROUTER_COUNT + 1))
  local result="$ROUTER_RESULTS/$ROUTER_COUNT" fixture_stdin
  # Bash otherwise replaces a background command's inherited stdin with /dev/null.
  exec {fixture_stdin}<&0
  (
    # Descendants stay in the worker group, including nested shell commands.
    set +m
    started=$SECONDS
    set +e
    ( set -e; "$@" ) > "$result.log" 2>&1
    status=$?
    printf '%s\n' "$status" > "$result.status"
    printf 'fixture %s %ss %s\n' "$status" "$((SECONDS - started))" "$name"
    # Keep successful logs too: the validator's evidence remains inspectable.
    cat "$result.log"
    exit 0
  ) <&"$fixture_stdin" &
  exec {fixture_stdin}<&-
}

router_fixtures_finish() {
  wait || true
  local index status failed=0
  for ((index=1; index<=ROUTER_COUNT; index++)); do
    status=missing
    if [[ -f "$ROUTER_RESULTS/$index.status" ]]; then
      read -r status < "$ROUTER_RESULTS/$index.status"
    fi
    if [[ "$status" != 0 ]]; then failed=$((failed + 1)); fi
  done
  echo "Router fixtures: $ROUTER_COUNT executed, $failed failed, $((SECONDS - ROUTER_STARTED))s wall."
  (( failed == 0 ))
}
