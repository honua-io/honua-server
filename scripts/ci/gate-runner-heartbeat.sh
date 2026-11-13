#!/usr/bin/env bash
# Read-only admission proof shared by producer and PR Gate selection.
set -uo pipefail

jq -cs --arg instance "$1" --argjson now "$(date +%s)" '
  def integer: if type == "number" then floor == . else false end;
  def unavailable: {state: "booting", heavy_ready: false};
  def heavy_available:
    if (.heavy | type) != "object" then false
    else .heavy.version == 1 and (.heavy.idle | integer)
      and .heavy.idle >= 1 and .heavy.idle <= 8 end;
  if length != 1 then unavailable else .[0] |
    if type != "object" then unavailable
    elif ($instance | startswith("i-") | not) or .instance != $instance
      or (.at | integer | not) or .at > $now or ($now - .at) > 90 then unavailable
    else
      {state: (if (.idle | integer) and .idle >= 2 then "ready" else "busy" end),
       heavy_ready: heavy_available}
    end
  end'
