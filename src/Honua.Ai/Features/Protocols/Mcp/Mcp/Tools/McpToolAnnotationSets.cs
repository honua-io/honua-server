// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Canonical <see cref="McpToolAnnotations"/> factories shared by every MCP tool
/// descriptor. Centralizing the read/write classification here keeps the hint
/// set consistent across tools and gives the taxonomy tests a single source of
/// truth: planning/grounding/validation/preview/query tools are
/// <c>readOnlyHint: true, destructiveHint: false</c>; write tools set
/// <c>readOnlyHint: false</c> and carry idempotency/destructiveness hints.
/// </summary>
internal static class McpToolAnnotationSets
{
    /// <summary>
    /// Read-only tool that operates only over the server's closed catalog
    /// (planning, grounding, validation, preview, layer/feature reads). Marks
    /// <c>readOnlyHint: true</c> and <c>destructiveHint: false</c>.
    /// </summary>
    public static McpToolAnnotations ReadOnly(string title) => new()
    {
        Title = title,
        ReadOnlyHint = true,
        DestructiveHint = false,
    };

    /// <summary>
    /// Read-only tool that reaches an open world of external entities (e.g.
    /// third-party geocoding/routing providers). Adds <c>openWorldHint: true</c>
    /// on top of the read-only hints.
    /// </summary>
    public static McpToolAnnotations ReadOnlyOpenWorld(string title) => new()
    {
        Title = title,
        ReadOnlyHint = true,
        DestructiveHint = false,
        OpenWorldHint = true,
    };

    /// <summary>
    /// Write tool. <paramref name="destructive"/> flags whether the write may
    /// destroy existing state; <paramref name="idempotent"/> flags whether
    /// replays with the same arguments have no additional effect.
    /// </summary>
    public static McpToolAnnotations Write(string title, bool destructive, bool idempotent) => new()
    {
        Title = title,
        ReadOnlyHint = false,
        DestructiveHint = destructive,
        IdempotentHint = idempotent,
    };
}
