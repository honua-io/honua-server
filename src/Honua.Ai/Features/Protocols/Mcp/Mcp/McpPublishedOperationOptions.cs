// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Options controlling whether — and how — validated operations-toolset descriptors
/// are published as first-class MCP tools (#2483, ADR-0056 Increment 4). Bound from
/// the <c>Mcp:PublishOperations</c> configuration section.
/// </summary>
/// <remarks>
/// Two independent switches (honua-server#3363):
/// <list type="bullet">
/// <item><description>
/// <see cref="AdminProjection"/> (default <see langword="true"/>) publishes the audited Admin
/// projection — the operations named by every registered
/// <see cref="Honua.Core.Features.Operations.Abstractions.IAuditedAdminMcpProjection"/>, which are
/// the committed <c>docs/gis/data/admin-mcp-projection-manifest.json</c> rows — as
/// <c>honua_admin_*</c> tools.
/// </description></item>
/// <item><description>
/// <see cref="Enabled"/> (default <see langword="false"/>) opts in to the full operations catalog,
/// including operation families whose MCP projection has not been audited.
/// </description></item>
/// </list>
/// Under either switch the audited
/// <see cref="Honua.Core.Features.Operations.Services.AdminMcpOperationExclusions"/> never publish,
/// descriptors already exposed by a hand-authored tool are skipped, and every call is governed by
/// the operation policy decision point. Published tools appear in the authenticated <c>full</c>
/// catalog export, never in the bounded default workflow view.
/// </remarks>
public sealed class McpPublishedOperationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Mcp:PublishOperations";

    /// <summary>
    /// Whether the full validated operations catalog is published as MCP tools. Default
    /// <see langword="false"/>: only the audited Admin projection (<see cref="AdminProjection"/>)
    /// publishes until an operator opts in to the full catalog.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the audited Admin operation projection is published as <c>honua_admin_*</c> tools.
    /// Default <see langword="true"/> (honua-server#3363); set
    /// <c>Mcp:PublishOperations:AdminProjection=false</c> to withhold it.
    /// </summary>
    public bool AdminProjection { get; set; } = true;

    /// <summary>
    /// "Deterministic mode": when <see langword="true"/>, only descriptors whose
    /// policy declares deterministic (AI-free) execution are published — the
    /// audit/inspect toolset with AI off. When <see langword="false"/> (default),
    /// AI-assisted descriptors are published too. Determinism is always surfaced on
    /// each published tool's output regardless of this flag.
    /// </summary>
    public bool DeterministicOnly { get; set; }
}
