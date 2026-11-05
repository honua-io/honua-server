// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;

namespace Honua.Ai.Protocols.Mcp;

// Served tools, resources, and templates need a registry descriptor, except operation-catalog tools bound through their operation descriptor; reverse coverage is omitted because composition is optional.
internal static class McpRegistryCompositionValidator
{
    public static IReadOnlyList<string> FindDrift(McpDataAccessSurface surface, ICapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(registry);

        var problems = new List<string>();

        var registryToolNames = registry.All
            .Where(d => d.McpToolName is not null)
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tool in surface.ToolHandlers.Where(tool => !registryToolNames.Contains(tool.Name)))
        {
            problems.Add(
                $"served /mcp tool '{tool.Name}' has no capability-registry descriptor");
        }

        var registryResourceUris = registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
            .SelectMany(d => d.ResourceUriTemplates)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var resource in surface.Resources)
        {
            foreach (var descriptor in resource.Describe().Where(d => !registryResourceUris.Contains(d.Uri)))
            {
                problems.Add(
                    $"served /mcp resource '{descriptor.Uri}' (family '{resource.Family}') "
                    + "has no capability-registry descriptor");
            }

            foreach (var template in resource.DescribeTemplates().Where(t => !registryResourceUris.Contains(t.UriTemplate)))
            {
                problems.Add(
                    $"served /mcp resource template '{template.UriTemplate}' "
                    + $"(family '{resource.Family}') has no capability-registry descriptor");
            }
        }

        return problems;
    }

    public static async Task<IReadOnlyList<string>> FindDriftAsync(
        McpDataAccessSurface surface,
        ICapabilityRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(registry);

        var problems = new List<string>();
        var registryToolNames = registry.All
            .Where(d => d.McpToolName is not null)
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);
        // codeql[cs/linq/missed-where]: awaits the live tool list and records problems for undescribed tools
        foreach (var tool in await surface.GetAllToolsAsync(cancellationToken).ConfigureAwait(false))
        {
            // A tool projected from the canonical operation catalog (#2483,
            // Mcp:PublishOperations) is bound through its operation descriptor, which
            // is that family's single source of truth; the registry cannot enumerate
            // the runtime catalog statically. Every other runtime tool source still
            // needs a registry descriptor (honua-server#3428).
            if (!registryToolNames.Contains(tool.Name) && tool is not Tools.PublishedOperationTool)
            {
                problems.Add($"served /mcp tool '{tool.Name}' has no capability-registry descriptor");
            }
        }

        var staticProblems = FindDrift(surface, registry);
        problems.AddRange(staticProblems.Where(p => !problems.Contains(p, StringComparer.Ordinal)));
        return problems;
    }
}
