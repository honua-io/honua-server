#!/usr/bin/env bash
# Extracts SQL statements from a version-1 seed YAML file and applies them
# via psql. Requires: python3 (or python), pyyaml, psql.
#
# Usage: apply-yaml-seed.sh <yaml-file>
#
# Environment variables (psql connection):
#   PGPASSWORD, PGHOST (default localhost), PGPORT (default 5432),
#   PGUSER (default honua), PGDATABASE (default honua_test)
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "Usage: $0 <yaml-file>" >&2
  exit 1
fi

YAML_FILE="$1"

# Resolve python3 or python, preferring python3 for portability.
if command -v python3 >/dev/null 2>&1; then
  PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON=python
else
  echo "Error: python3 or python is required." >&2
  exit 1
fi

if ! "$PYTHON" -c "import yaml" 2>/dev/null; then
  echo "Error: pyyaml is required. Install with: $PYTHON -m pip install pyyaml" >&2
  exit 1
fi

SQL_TMP="$(mktemp /tmp/yaml-seed-XXXXXX.sql)"
trap 'rm -f "$SQL_TMP"' EXIT

"$PYTHON" - "$YAML_FILE" > "$SQL_TMP" <<'PYTHON'
import sys, yaml
with open(sys.argv[1]) as f:
    data = yaml.safe_load(f)
if data.get('version') != 1 or 'sql' not in data:
    sys.exit(f"Invalid seed YAML: expected version: 1 with a top-level sql key in {sys.argv[1]}")
for stmt in data['sql']:
    s = stmt.strip()
    if not s.endswith(';'):
        s += ';'
    print(s)
    print()
PYTHON

PGPASSWORD="${PGPASSWORD:-honua}" psql \
  -v ON_ERROR_STOP=1 \
  -h "${PGHOST:-localhost}" \
  -p "${PGPORT:-5432}" \
  -U "${PGUSER:-honua}" \
  -d "${PGDATABASE:-honua_test}" \
  -f "$SQL_TMP"
