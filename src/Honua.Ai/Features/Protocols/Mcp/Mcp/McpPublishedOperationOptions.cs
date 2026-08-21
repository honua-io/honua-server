// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Options controlling whether — and how — validated operations-toolset descriptors
/// are published as first-class MCP tools (#2483, ADR-0056 Increment 4). Bound from
/// the <c>Mcp:PublishOperations</c> configuration section.
/// </summary>
/// <remarks>
/// Deterministic <c>admin.*</c> descriptors are published by default on a host that
/// composes this source. Other operation families remain opt-in. Each descriptor
/// comes from the canonical operation catalog and is projected into a typed,
/// cacheable, policy-governed MCP tool.
/// </remarks>
public sealed class McpPublishedOperationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Mcp:PublishOperations";

    /// <summary>
    /// Whether every validated operation family is published as MCP tools. Default
    /// <see langword="false"/>: only the separately controlled <c>admin.*</c> family
    /// is projected until an operator opts other families in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether deterministic <c>admin.*</c> descriptors are published. Defaults to
    /// <see langword="true"/> for server hosts that compose the admin operation
    /// catalog; set false to remove the family from <c>tools/list</c>.
    /// </summary>
    public bool AdminFamilyEnabled { get; set; } = true;

    /// <summary>
    /// "Deterministic mode": when <see langword="true"/>, only descriptors whose
    /// policy declares deterministic (AI-free) execution are published — the
    /// audit/inspect toolset with AI off. This defaults to <see langword="true"/>;
    /// setting it to <see langword="false"/> explicitly opts AI-assisted descriptors
    /// into publication. Determinism is always surfaced on
    /// each published tool's output regardless of this flag.
    /// </summary>
    public bool DeterministicOnly { get; set; } = true;
}
