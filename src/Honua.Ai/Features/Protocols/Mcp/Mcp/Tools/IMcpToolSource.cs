// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// A source of dynamic, runtime-published MCP tools that the
/// <see cref="McpOperatorSurface"/> merges into <c>tools/list</c> and
/// <c>tools/call</c> in addition to the statically-registered
/// <see cref="IMcpTool"/> set (#2483, ADR-0056 Increment 4).
/// </summary>
/// <remarks>
/// <para>
/// The statically-registered tools are the built-in, capability-registry-bound
/// catalog (every one has an <c>ICapabilityRegistry</c> descriptor, enforced at
/// startup). A tool source instead projects tools that are <em>published at
/// runtime</em> — for example a validated operations-toolset descriptor becoming a
/// first-class tool — so it can change without a redeploy. Dynamic tools are kept
/// out of the static catalog on purpose: they are not part of the registry-bound
/// built-in contract, and a statically-registered tool of the same name always
/// wins.
/// </para>
/// <para>
/// A host with no tool source composed behaves exactly as before: the surface
/// merges an empty dynamic set.
/// </para>
/// </remarks>
internal interface IMcpToolSource
{
    /// <summary>
    /// Returns the currently-published dynamic tools. Called on each
    /// <c>tools/list</c> and on a <c>tools/call</c> that misses the static catalog,
    /// so implementations must be cheap (return quickly when disabled, and lean on a
    /// cached snapshot for the underlying catalog).
    /// </summary>
    ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken);
}
