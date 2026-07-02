// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Composition conformance check for the unified capability registry
/// (ADR-0058, Decision B / honua-server B2 #2334). It verifies the served
/// <c>/mcp</c> catalog is bound to the registry: every advertised tool name and
/// every advertised resource URI (concrete or template) must be described by a
/// <see cref="CapabilityDescriptor"/> in the <see cref="ICapabilityRegistry"/> —
/// no tool or resource is served without registry provenance.
/// </summary>
/// <remarks>
/// This is the "nothing served without a registry descriptor" direction, which
/// holds for any host regardless of which optional tools it wired. The reverse —
/// every registry descriptor is served — is environment dependent (geocoding,
/// routing, Metadata v2, and the promotion resources are conditionally composed),
/// so it is asserted against the full roster in the unit tests
/// (<c>CapabilityRegistryConformanceTests</c>) rather than at runtime.
/// </remarks>
internal static class McpRegistryCompositionValidator
{
    /// <summary>
    /// Returns the drift between the served <paramref name="surface"/> catalog and
    /// the <paramref name="registry"/> roster: one human-readable message per
    /// served tool/resource that has no registry descriptor. An empty list means
    /// the served catalog is fully bound to the registry.
    /// </summary>
    /// <param name="surface">The live MCP operator surface (the served catalog).</param>
    /// <param name="registry">The unified capability registry (source of truth).</param>
    public static IReadOnlyList<string> FindDrift(McpOperatorSurface surface, ICapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(registry);

        var problems = new List<string>();

        var registryToolNames = registry.All
            .Where(d => d.McpToolName is not null)
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tool in surface.ToolHandlers)
        {
            if (!registryToolNames.Contains(tool.Name))
            {
                problems.Add(
                    $"served /mcp tool '{tool.Name}' has no capability-registry descriptor");
            }
        }

        var registryResourceUris = registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
            .SelectMany(d => d.ResourceUriTemplates)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var resource in surface.Resources)
        {
            foreach (var descriptor in resource.Describe())
            {
                if (!registryResourceUris.Contains(descriptor.Uri))
                {
                    problems.Add(
                        $"served /mcp resource '{descriptor.Uri}' (family '{resource.Family}') "
                        + "has no capability-registry descriptor");
                }
            }

            foreach (var template in resource.DescribeTemplates())
            {
                if (!registryResourceUris.Contains(template.UriTemplate))
                {
                    problems.Add(
                        $"served /mcp resource template '{template.UriTemplate}' "
                        + $"(family '{resource.Family}') has no capability-registry descriptor");
                }
            }
        }

        return problems;
    }
}
