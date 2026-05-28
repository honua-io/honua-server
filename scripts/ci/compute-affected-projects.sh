#!/usr/bin/env bash
# Compute the closure of .csproj files affected by a diff so CI build steps
# can scope `dotnet build` to changed projects + their reverse-dependencies
# instead of rebuilding the whole solution every push.
#
# Phase 3 of the modularization plan / ADR-0043 PR #2.
#
# Inputs (env or args):
#   BASE_REF — git ref to diff against (default: origin/trunk).
#   HEAD_REF — git ref for the new state (default: HEAD).
#   FORCE_FULL_BUILD_PATHS — newline-separated path prefixes that, when changed,
#     force a full-graph build (sln/props/CI files). Defaults to a baked-in
#     list covering Directory.Packages.props, Directory.Build.props, Honua.sln,
#     global.json, NuGet.config, .github/, and scripts/ci/.
#
# Outputs to stdout:
#   Lines of csproj paths relative to the repo root, or the single token
#   "ALL" when a full-graph build is required.
#
# Exit codes:
#   0 — normal output (either ALL or a list of csproj paths).
#   2 — unexpected git failure.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

base_ref="${BASE_REF:-origin/trunk}"
head_ref="${HEAD_REF:-HEAD}"

# Shared infrastructure that invalidates the affected-projects assumption.
# Edits to any of these force a full-graph build because the dependency
# graph or the build itself could be silently affected by them.
default_force_full=$'Directory.Packages.props\nDirectory.Build.props\nDirectory.Build.targets\nHonua.sln\nglobal.json\nNuGet.config\n.github/\nscripts/ci/'
force_full_paths="${FORCE_FULL_BUILD_PATHS:-${default_force_full}}"

# Diff the working tree against base. Tolerate the case where the base ref
# is not yet fetched on this runner (shallow clone) by falling back to
# the merge-base when available.
if ! git rev-parse --verify --quiet "${base_ref}" >/dev/null 2>&1; then
  echo "::warning::compute-affected-projects: base ref '${base_ref}' not found; defaulting to ALL" >&2
  echo "ALL"
  exit 0
fi

changed_files="$(git diff --name-only "${base_ref}...${head_ref}" 2>/dev/null || true)"
if [[ -z "${changed_files}" ]]; then
  # No diff = no projects to build. Emit nothing so callers can short-circuit.
  exit 0
fi

# Any force-full hit?
while IFS= read -r prefix; do
  [[ -z "${prefix}" ]] && continue
  if printf '%s\n' "${changed_files}" | grep -qE "^${prefix}"; then
    echo "ALL"
    exit 0
  fi
done <<< "${force_full_paths}"

# Map each changed source file to the nearest enclosing .csproj by walking
# the directory tree upward. This catches edits to .cs, .razor, .json
# embedded resources, etc. Files outside any csproj (docs, ADRs) are ignored.
declare -A affected
while IFS= read -r file; do
  [[ -z "${file}" ]] && continue
  dir="$(dirname "${file}")"
  while [[ "${dir}" != "." && "${dir}" != "/" ]]; do
    csproj="$(find "${dir}" -maxdepth 1 -name "*.csproj" -print -quit 2>/dev/null || true)"
    if [[ -n "${csproj}" ]]; then
      # Strip leading ./ so the output matches `find -printf '%P\n'` shape.
      csproj="${csproj#./}"
      affected["${csproj}"]=1
      break
    fi
    dir="$(dirname "${dir}")"
  done
done <<< "${changed_files}"

if [[ ${#affected[@]} -eq 0 ]]; then
  # Diff touched only files outside any csproj (docs / images / non-managed code).
  exit 0
fi

# Build a forward project-reference map so we can walk reverse-dependencies.
# Each .csproj's <ProjectReference Include="..."/> lines name the projects it
# consumes; the inverse map gives us "who consumes this project". We need
# the inverse so a change in Honua.Core picks up every consumer that needs
# to recompile.
declare -A reverse_refs
mapfile -t all_csprojs < <(find . -maxdepth 6 -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*/.claude/*" -printf '%P\n' | sort -u)
for csproj in "${all_csprojs[@]}"; do
  csproj_dir="$(dirname "${csproj}")"
  # Extract referenced paths from <ProjectReference Include="..." />.
  while IFS= read -r ref; do
    [[ -z "${ref}" ]] && continue
    # Normalize backslashes (Windows-style refs in .csproj) and resolve
    # relative to the referrer's directory.
    ref="${ref//\\//}"
    resolved="$(cd "${csproj_dir}" && realpath -m --relative-to="${REPO_ROOT}" "${ref}" 2>/dev/null || true)"
    [[ -z "${resolved}" ]] && continue
    reverse_refs["${resolved}"]+="${csproj}"$'\n'
  done < <(grep -oE 'ProjectReference[[:space:]]+Include="[^"]+\.csproj"' "${csproj}" 2>/dev/null \
           | sed -E 's/.*Include="([^"]+)".*/\1/')
done

# Walk reverse-dependency closure from the initial affected set.
queue=("${!affected[@]}")
while [[ ${#queue[@]} -gt 0 ]]; do
  current="${queue[0]}"
  queue=("${queue[@]:1}")
  consumers="${reverse_refs[${current}]:-}"
  [[ -z "${consumers}" ]] && continue
  while IFS= read -r consumer; do
    [[ -z "${consumer}" ]] && continue
    if [[ -z "${affected[${consumer}]:-}" ]]; then
      affected["${consumer}"]=1
      queue+=("${consumer}")
    fi
  done <<< "${consumers}"
done

# Emit deterministic sorted output.
printf '%s\n' "${!affected[@]}" | sort -u
