// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Net.Http;
using Honua.Core.Features.Capabilities;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Honua.Infrastructure.Capabilities;

// The document drops only operations the runtime filter answers with 404, so a 402 entitlement gate stays advertised.
internal sealed class CapabilityGateOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (document.Paths is null || document.Paths.Count == 0)
        {
            return Task.CompletedTask;
        }

        // The registry is the single evaluation entry point; without it there is
        // nothing to resolve against and the document is left untouched.
        var registry = context.ApplicationServices.GetService<ICapabilityRegistry>();
        if (registry is null)
        {
            return Task.CompletedTask;
        }

        var flags = context.ApplicationServices.GetService<IOptions<CapabilityFlagOptions>>()?.Value
            ?? new CapabilityFlagOptions();
        var gateContext = new CapabilityGateContext { ExperimentalFlags = flags };

        foreach (var group in context.DescriptionGroups)
        {
            foreach (var apiDescription in group.Items)
            {
                var gate = GetGate(apiDescription);
                if (gate is null)
                {
                    continue;
                }

                var resolution = registry.Resolve(gate.DescriptorId, gateContext);

                // Only the experimental-disabled outcome is pruned here — the same
                // single outcome the endpoint filter (T5) short-circuits with 404.
                if (resolution.Enabled ||
                    !string.Equals(
                        resolution.ReasonCode,
                        CapabilityReasonCodes.ExperimentalDisabled,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                DropOperation(document, apiDescription);
            }
        }

        return Task.CompletedTask;
    }

    private static CapabilityGateMetadata? GetGate(ApiDescription apiDescription)
    {
        var metadata = apiDescription.ActionDescriptor?.EndpointMetadata;
        if (metadata is null)
        {
            return null;
        }

        // Metadata added last (closest to the endpoint) wins, mirroring how the
        // endpoint filter reads the most-specific CapabilityGateMetadata.
        for (var i = metadata.Count - 1; i >= 0; i--)
        {
            if (metadata[i] is CapabilityGateMetadata gate)
            {
                return gate;
            }
        }

        return null;
    }

    private static void DropOperation(OpenApiDocument document, ApiDescription apiDescription)
    {
        var pathKey = FindPathKey(document, apiDescription.RelativePath);
        if (pathKey is null ||
            document.Paths[pathKey] is not { Operations: { } operations })
        {
            return;
        }

        if (!string.IsNullOrEmpty(apiDescription.HttpMethod))
        {
            operations.Remove(HttpMethod.Parse(apiDescription.HttpMethod));
        }

        // A path with no operations left should not linger in the document.
        if (operations.Count == 0)
        {
            document.Paths.Remove(pathKey);
        }
    }

    private static string? FindPathKey(OpenApiDocument document, string? relativePath)
    {
        var target = NormalizeKey(relativePath);
        return document.Paths
            .Where(entry => string.Equals(NormalizeKey(entry.Key), target, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .FirstOrDefault();
    }

    private static string NormalizeKey(string? path)
        => string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
}
