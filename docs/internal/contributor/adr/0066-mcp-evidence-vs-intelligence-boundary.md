# ADR-0066: Evidence-vs-Intelligence Boundary for the Open MCP Surface

## Status
Accepted

## Context

`honua-server` serves a public MCP surface over `POST /mcp`. Its JSON-RPC
dispatcher and registry was named `McpOperatorSurface`. That name is wrong, and
the way it is wrong matters for licensing and positioning, not just aesthetics.

What the surface actually dispatches:

- **~27 studio / analysis / publish tools** — `honua_render_map`,
  `honua_get_style`, `honua_apply_style_preset`, `honua_list_layers`,
  `honua_query_features`, `honua_describe_layer`, the geocode/route tools, the
  seven plan/execute tools (`honua_plan_analysis`, `honua_validate_plan`,
  `honua_dry_run_plan`, `honua_execute_plan`, `honua_cancel_job`,
  `honua_list_jobs`, grounding), and the authoring/packaging tools. This is the
  implementation of the `geospatial-mcp` open standard, which is scoped to
  *"analyst, map, and app-builder workflows"* — i.e. studio.
- **8 bounded, read-only ops *evidence* tools** — `honua_ops_health`,
  `honua_ops_findings`, `honua_alert_events`, `honua_operate_events`,
  `honua_platform_release_status`, `honua_deploy_operations`,
  `honua_supported_operation_kinds`, `honua_propose_rollback`. These read
  operational state and, at most, *propose* a control-plane action that still
  resolves through the Console approval inbox (ADR-0062 graduated autonomy;
  MCP never approves its own proposals).
- **Zero operator intelligence.** No diagnosis, no tuning, no upgrade planning,
  no GitOps rollout, no remediation reasoning lives here.

The real boundary is **evidence vs. intelligence**, not operator vs. studio.
This repo serves bounded, read-only operational *evidence* openly. The
*intelligence* that reasons over that evidence and acts on it — diagnose, tune,
upgrade planning with rollback gates, GitOps rollout, remediation planning,
requirements analysis — lives in `honua-devops`, which is private and
proprietary. `honua-devops` exposes its own ~35-tool operator surface over MCP
stdio (`--mcp`) and consumes *our* evidence tools through its
`honua_observe_diagnose_propose` day-2 loop.

The name `McpOperatorSurface`, in the public repo, implies the operator surface
is open-core. It is not. ADR-0024 (Open-Core Edition Model) draws this line
explicitly: the open-core promise covers "the runtime, protocols, SDKs,
deployment targets, and base MCP **data-access** surface"; "higher-level
operator/copilot tooling may remain private." `honua-devops`'s own README
restates it: *"`honua-devops` is private operator tooling and is not part of
Honua's open-core runtime promise… Public/open surfaces remain in `honua-server`,
the official SDK repos, the mobile repos, and the base MCP data-access surface."*

A dispatcher literally named for the *operator* surface, sitting in the public
repo, invites the exact wrong conclusion about where the proprietary line sits.
That already happened once: during a competitive-analysis pass the tool list was
read as "operator-first" when it is in fact studio-dominant with a bounded
evidence wing. The misread costs us twice — it obscures what is actually
defensible (the operator intelligence), and it implies we are giving away
something we are not.

This ADR writes the boundary down first, then lets the boundary pick the name —
so we do not choose a second name that ages as badly as the first.

## Decision

### 1. The boundary: evidence stays open, intelligence stays proprietary

| | Evidence (open) | Intelligence (proprietary) |
|---|---|---|
| **Where** | `honua-server`, this MCP surface + the SDK data-access MCP package (`@honua/mcp-server`) | `honua-devops` (private) |
| **What** | Studio/data-access tools + 8 bounded read-only ops-evidence tools | Diagnose, tune, upgrade planning with rollback gates, GitOps rollout, remediation planning, requirements analysis |
| **Transport** | HTTP `POST /mcp` (Streamable HTTP), authenticated per ADR-0026 | MCP stdio (`--mcp`), ~35 operator tools |
| **Posture** | Read state; at most *propose* an action that a human approves through Console (ADR-0062) | Reasons over evidence and drives the day-2 loop; consumes our evidence tools via `honua_observe_diagnose_propose` |
| **Licensing** | Open-core (ELv2), included in Community (ADR-0024) | Private enterprise tooling on top of the public control-plane API |

The 8 ops-evidence tools are **correctly public**, not a leak across the line.
They expose bounded, read-only operational *facts* and human-gated *proposals*;
they contain none of the reasoning that constitutes the operator moat. Removing
them would break the evidence path that `honua_observe_diagnose_propose` depends
on and would protect nothing that is actually proprietary. Evidence is the
substrate the intelligence stands on; publishing the substrate is the open-core
promise working as designed, consistent with the two-plane operability model
(ADR-0060) and the fix-forward operate model (ADR-0059).

### 2. The name: `McpDataAccessSurface`

The dispatcher/registry type and its registration surface are renamed:

- `McpOperatorSurface` → **`McpDataAccessSurface`**
- `AddMcpOperatorSurface` → `AddMcpDataAccessSurface`
- `MapMcpOperatorSurface` → `MapMcpDataAccessSurface`

`DataAccessSurface` is the canonical open-core term already in use for exactly
this thing: ADR-0024 and the `honua-devops` README both call it the **"base MCP
data-access surface."** Naming the type to match the licensing language it
implements makes the boundary self-documenting — the public surface is named for
the open side of the line, and nothing in the public repo claims the operator
surface. The name describes what the surface *is* (bounded data access plus
evidence), not the intelligence it deliberately *is not*.

### 3. Scope guardrails (what this decision does NOT do)

This is a **mechanical rename plus documentation**. It does not:

- add, remove, rename, or re-gate any tool; `tools/list` output is
  byte-identical before and after;
- change any wire-visible behavior — method names, session semantics, the
  `/mcp` route, error contracts, and JSON-RPC framing are untouched;
- move the 8 ops-evidence tools out of this repo (they are deliberately public);
- split the MCP surface into two endpoints or two assemblies;
- change anything in `honua-devops`; or
- revisit ADR-0028 (no AI-driven data editing).

## Consequences

### Positive

- **The proprietary line is legible in the code.** A contributor, evaluator, or
  future maintainer reading the public tool registry no longer infers that the
  operator surface is open-core. The type name now agrees with ADR-0024 and the
  `honua-devops` README.
- **The moat is described accurately.** Evidence vs. intelligence names what is
  actually defensible (the reasoning in `honua-devops`) and what is deliberately
  open (bounded evidence + studio), so competitive positioning stops turning on
  a misread of the tool list.
- **Cheap now, expensive later.** The name is corrected before it propagates
  further into docs, SDK-facing terminology, and third-party integrations.

### Negative / costs

- A class rename touches every reference (dispatcher, registration extensions,
  `<see cref>` docs, log category, and tests). The compiler enforces
  completeness, and CI proves `tools/list` is unchanged.
- Historical ADRs (0054, 0056) that referenced the old identifier are updated to
  the new one so `grep` stays coherent; they remain otherwise as-authored.

### Neutral

- No release or deploy impact. No SDK-published type changes: `McpDataAccessSurface`
  and its registration extensions are `internal` to `Honua.Ai` and are not
  exported to `honua-sdk-dotnet` / `honua-sdk-js`, so no consumer coordination is
  required. The SDK-hosted data-access MCP package (`@honua/mcp-server`) is a
  separate, already-open surface and is unaffected.

### Related decisions

- ADR-0024 — Open-Core Edition Model (the licensing line this rename honors).
- ADR-0026 — AI-First Operator Contract (the authenticated MCP contract).
- ADR-0059 — First-Release Scope and Fix-Forward Operate Model.
- ADR-0060 — Two-Plane Operability Architecture.
- ADR-0062 — Graduated Autonomy Policy for Ops Findings (why proposals are
  human-gated).
- ADR-0028 — AI-Driven Data Editing Is Not Allowed (explicitly not revisited).
