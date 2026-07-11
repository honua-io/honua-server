#!/usr/bin/env bash

set -euo pipefail

if [[ "$#" -lt 2 || "$#" -gt 4 ]]; then
    echo "Usage: $0 <manifest.json> <head-sha> [server-current-ref] [current-only]" >&2
    exit 2
fi

manifest="$1"
head_sha="$2"
server_current_ref="${3:-}"
current_only="${4:-false}"

if [[ "$current_only" != "true" && "$current_only" != "false" ]]; then
    echo "current-only must be true or false" >&2
    exit 2
fi

jq -e '
  . as $manifest
  | ($manifest.schemaVersion == 1)
  and ($manifest.adminApiMajor | type == "string" and length > 0)
  and ($manifest.matrixDepth == 3)
  and ($manifest.serverRefs | type == "array" and length == $manifest.matrixDepth)
  and ($manifest.sdkSetVersions | type == "array" and length == $manifest.matrixDepth)
  and all($manifest.serverRefs[];
    (.label | type == "string" and length > 0)
    and (.ref | type == "string" and length > 0)
    and (.capabilities.migrationAutomation | type == "boolean"))
  and all($manifest.sdkSetVersions[];
    (.label | type == "string" and length > 0)
    and (.refs.js | type == "string" and length > 0)
    and (.refs.python | type == "string" and length > 0)
    and (.refs.dotnet | type == "string" and length > 0)
    and (.capabilities.migrationAutomation | type == "boolean"))
' "$manifest" >/dev/null

jq -c \
  --arg head "$head_sha" \
  --arg serverCurrentRef "$server_current_ref" \
  --argjson currentOnly "$current_only" '
    def serverByLabel($manifest; $name):
      (($manifest.serverRefs[] | select(.label == $name)) // error("Unknown server label: " + $name))
      | if .label == "current" and ($serverCurrentRef | length) > 0
        then . + { ref: $serverCurrentRef }
        else .
        end;
    def sdkByLabel($manifest; $name):
      ($manifest.sdkSetVersions[] | select(.label == $name)) // error("Unknown SDK set label: " + $name);
    def checkoutRef($ref):
      if $ref == "HEAD" then $head else $ref end;
    def cell($manifest; $status; $expectedSupported):
      . as $cell
      | (serverByLabel($manifest; $cell.server)) as $server
      | (sdkByLabel($manifest; $cell.sdk)) as $sdk
      | {
          server_label: $server.label,
          server_ref: $server.ref,
          server_checkout_ref: checkoutRef($server.ref),
          server_channel: $server.channel,
          sdk_label: $sdk.label,
          sdk_channel: $sdk.channel,
          sdk_js_ref: $sdk.refs.js,
          sdk_python_ref: $sdk.refs.python,
          sdk_dotnet_ref: $sdk.refs.dotnet,
          migration_automation_required: (
            $server.capabilities.migrationAutomation == true
            and $sdk.capabilities.migrationAutomation == true
          ),
          cell_status: $status,
          expected_supported: $expectedSupported
        };
    . as $manifest
    | {
        include: (
          (
            (($manifest.matrix.supported // []) | map(cell($manifest; "supported"; true)))
            + (($manifest.matrix.evaluation // []) | map(cell($manifest; "evaluation"; false)))
          )
          | if $currentOnly
            then map(select(.server_label == "current" and .sdk_label == "sdk-current"))
            else .
            end
        )
      }
  ' "$manifest"
