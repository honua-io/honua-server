// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Options controlling whether — and how — validated operations-toolset descriptors
/// are published as first-class MCP tools (#2483, ADR-0056 Increment 4). Bound from
/// the <c>Mcp:PublishOperations</c> configuration section.
/// </summary>
/// <remarks>
/// This is off by default so no host changes its advertised <c>tools/list</c> until
/// an operator explicitly opts in. When enabled, each descriptor in the operations
/// catalog (except those already exposed by a hand-authored tool) is projected into
/// a typed, cacheable, policy-governed MCP tool named <c>honua_op_{operationId}</c>.
/// </remarks>
public sealed class McpPublishedOperationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Mcp:PublishOperations";

    /// <summary>
    /// Whether validated operation descriptors are published as MCP tools. Default
    /// <see langword="false"/>: the operations catalog is not projected onto
    /// <c>tools/list</c> until an operator turns this on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// "Deterministic mode": when <see langword="true"/>, only descriptors whose
    /// policy declares deterministic (AI-free) execution are published — the
    /// audit/inspect toolset with AI off. When <see langword="false"/> (default),
    /// AI-assisted descriptors are published too. Determinism is always surfaced on
    /// each published tool's output regardless of this flag.
    /// </summary>
    public bool DeterministicOnly { get; set; }
}
