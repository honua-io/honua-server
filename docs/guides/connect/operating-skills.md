---
type: guide
title: "Honua operating skills"
description: "Use the quickstart's task-specific Agent Skills and verify their packaged bytes."
---
# Honua operating skills

The Compose quickstart carries five [Agent Skills](https://agentskills.io/specification)
under `skills/`, alongside `AGENTS.md`. Each directory contains `SKILL.md` and
supporting references. Installation is explicit; the package does not change
your home directory or permissions.

| Skill | Use it for | Workflow family |
| --- | --- | --- |
| `honua-install` | Install, readiness and secret files | Automate / Deploy |
| `honua-ops` | Findings, approved restarts, upgrades and rollback | Automate / Deploy |
| `honua-services` | Import, publish and verify filtered results | Publish Data |
| `honua-studio-maps` | Saved styles, map versions and audience | Build App |
| `honua-tuning` | Measured publication budgets | Automate / Deploy |

These workflow families align with geospatial-mcp's skills catalog. Its
visualization, composition, query-shaping and publishing skills complement
these Honua-specific operating workflows. The Honua files remain canonical in
the server repository's `skills/` directory. Packaging copies their bytes without
templating, line-ending conversion or a separately maintained version.

From the trusted, extracted quickstart directory, verify the agent files:

```bash
sha256sum -c agent-files.sha256
```

The lock covers `AGENTS.md` and every supporting skill file. The release archive
also has its published SHA-256 checksum. Verify that archive against the release
before trusting its internal lock: matching hashes establish consistency, not
the trustworthiness of instructions. Site edits to `AGENTS.md` change its digest.

## Install for Claude Code and Codex

From the trusted, extracted quickstart directory, install the same canonical
files for either host (or both). These commands copy all skills and their
references without changing their bytes:

```bash
mkdir -p .claude/skills
cp -R skills/. .claude/skills/
mkdir -p .agents/skills
cp -R skills/. .agents/skills/
```

Claude Code discovers project skills in `.claude/skills`; Codex discovers them in
`.agents/skills` ([OpenAI documentation](https://learn.chatgpt.com/docs/build-skills)).
Run the host from this project directory. Review any existing
`honua-*` directories before copying: these commands replace matching files.
The source stays in `skills/`; repeat the copy after upgrading the package.

## MCP host compatibility

Honua also advertises `io.modelcontextprotocol/skills` through MCP
`server/discover` on protocol revision `2026-07-28`. Clients can page through
`skills/list`, fetch one manifest with `skills/get`, then read every listed file
using `resources/read`. Files are embedded from the canonical `skills/` directory
at build time; a deployed release serves the same bytes as its quickstart package.
The legacy MCP revision does not advertise the extension.

Each manifest includes the complete frontmatter and every supporting file's URI,
SHA-256 digest and byte size. Canonical skills use JSON objects in their YAML
frontmatter, preserving all fields in the MCP manifest without a separate metadata
copy. File reads return complete files; cursor and `maxChars` parameters are
rejected for skill resources so digests remain verifiable.

Consult the [MCP Skills extension](https://modelcontextprotocol.io/extensions/skills/overview)
and its [client matrix](https://modelcontextprotocol.io/extensions/client-matrix)
for host support. A consuming host must verify every file against its retained
manifest, preserve the server origin and apply its execution approval policy.
Reading `SKILL.md` alone grants no permission. Resource reads use Honua's existing
authentication and tenant checks.

## Behavioral evaluation

Format validation and package byte checks do not measure agent task success.
A separate evaluation needs a pinned guide-only baseline and a guide-plus-skills
arm, the same product and client revisions, equivalent permissions and tasks,
and per-stage measured outcomes. The original `honua-ops` and `honua-services` skills stayed after one informal
local comparison on a quickstart. That comparison did not pin product revisions,
client versions, or file hashes. The notes are
[operating-skills-ab.md](https://github.com/honua-io/honua-server/blob/213e7ef3bf243fddf30c06a4cdb4cac737b9bfc8/docs/internal/evidence/operating-skills-ab.md).
The record is not part of the quickstart package.
