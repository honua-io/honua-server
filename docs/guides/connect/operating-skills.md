---
type: guide
title: "Honua operating skills"
description: "Use the quickstart's task-specific Agent Skills and verify their packaged bytes."
---
# Honua operating skills

The Compose quickstart carries five [Agent Skills](https://agentskills.io/specification)
under `skills/`, alongside `AGENTS.md`. Each directory contains `SKILL.md` and
supporting references. Load the relevant skill using your coding host's supported
filesystem skill mechanism. Host-specific installation locations differ; the
package does not silently install into your home directory or change permissions.

| Skill | Use it for | Workflow family |
| --- | --- | --- |
| `honua-install` | Install, readiness, secret files | Automate / Deploy |
| `honua-ops` | Findings, approved restarts, upgrades and rollback | Automate / Deploy |
| `honua-services` | Import, publish and verify filtered results | Publish Data |
| `honua-studio-maps` | Studio styles, saved versions and audience | Build App |
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

## MCP host compatibility

Filesystem skills and the MCP Skills extension are separate capabilities.
The skills in this package can be used as filesystem skills; this content change
does not add `skills/list` or `skills/get` to Honua's MCP endpoint.

The [extension client matrix](https://modelcontextprotocol.io/extensions/client-matrix)
currently marks ChatGPT plugin import, fast-agent and MCP Inspector as partial.
The broader [implementations list](https://github.com/modelcontextprotocol/ext-skills/blob/main/docs/implementations.md)
reports released-extension support in fast-agent, MCPJam Inspector, mcpc, vitehub
and harness. ChatGPT plugin import takes a static submission snapshot. The two
lists differ in granularity; consult each host's linked implementation before
relying on a particular workflow. Neither list establishes MCP Skills parity
for Codex or Claude Code. Local `SKILL.md` support does not imply remote skill
verification, origin handling or approval support.

A host consuming future MCP-served skills must verify each file's digest and
byte size against its retained manifest, preserve the server origin and apply
its execution approval policy. Reading `SKILL.md` alone grants no permission.

## Behavioral evaluation

Format validation and package byte checks do not measure agent task success.
A separate evaluation needs a pinned guide-only baseline and a guide-plus-skills
arm, the same product and client revisions, equivalent permissions and tasks,
and per-stage measured outcomes. That evaluation is separate from this package.
