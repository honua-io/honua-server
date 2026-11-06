---
type: guide
title: "Honua operating skills"
description: "Use the quickstart's task-specific Agent Skills and verify their packaged bytes."
---
# Honua operating skills

The Compose quickstart carries two [Agent Skills](https://agentskills.io/specification)
under `skills/`, alongside `AGENTS.md`. Each directory contains `SKILL.md` and
supporting references. Installation is explicit; the package does not change
your home directory or permissions.

| Skill | Use it for | Workflow family |
| --- | --- | --- |
| `honua-ops` | Findings, approved restarts, upgrades and rollback | Automate / Deploy |
| `honua-services` | Import, publish and verify filtered results | Publish Data |

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
files for either host (or both). These commands copy both skills and their
references without changing their bytes:

```bash
mkdir -p .claude/skills
cp -R skills/. .claude/skills/
mkdir -p .agents/skills
cp -R skills/. .agents/skills/
```

Claude Code discovers project skills in `.claude/skills`; Codex discovers them in
`.agents/skills`. Run the host from this project directory. Review any existing
`honua-*` directories before copying: these commands replace matching files.
The source stays in `skills/`; repeat the copy after upgrading the package.

## MCP host compatibility

Filesystem skills and the MCP Skills extension are separate capabilities.
The skills in this package can be used as filesystem skills; this content change
does not add `skills/list` or `skills/get` to Honua's MCP endpoint.

Remote delivery is separate work. Consult the
[MCP Skills extension](https://modelcontextprotocol.io/extensions/skills/overview)
and its [client matrix](https://modelcontextprotocol.io/extensions/client-matrix)
for host support; local skill discovery does not establish extension conformance.

A host consuming future MCP-served skills must verify each file's digest and
byte size against its retained manifest, preserve the server origin and apply
its execution approval policy. Reading `SKILL.md` alone grants no permission.

## Behavioral evaluation

Format validation and package byte checks do not measure agent task success.
A separate evaluation needs a pinned guide-only baseline and a guide-plus-skills
arm, the same product and client revisions, equivalent permissions and tasks,
and per-stage measured outcomes. The two skills above stayed after one informal
local comparison on a quickstart. That comparison did not pin product revisions,
client versions, or file hashes. The notes are
[operating-skills-ab.md](../../internal/evidence/operating-skills-ab.md).
The record is not part of the quickstart package.
