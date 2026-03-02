#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

exec "$REPO_ROOT/infrastructure/terraform/scripts/azure/run-azure-terraform-integration.sh" "$@"
