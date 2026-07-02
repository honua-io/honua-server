// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http;
using Honua.Core.Features.Capabilities;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Honua.Infrastructure.Capabilities;

/// <summary>
/// OpenAPI document transformer that prunes operations gated on a capability that
/// resolves <b>experimental-disabled</b> (Track T7 / #2343). It is the description
/// layer's counterpart to the runtime <see cref="CapabilityGateEndpointFilter"/>
/// (Track T5): the filter returns <c>404</c> for a disabled-experimental route, and
/// this transformer removes the matching operation from the generated OpenAPI
/// document so the published contract never advertises an endpoint that 404s.
/// </summary>
/// <remarks>
/// <para>
/// For every <see cref="ApiDescription"/> that carries a
/// <see cref="CapabilityGateMetadata"/> marker (attached by <c>WithCapabilityGate</c>),
/// the transformer resolves the descriptor id through the <see cref="ICapabilityRegistry"/>
/// with the config-driven experimental precedence (#2339 / T2). Only the
/// <see cref="CapabilityReasonCodes.ExperimentalDisabled"/> outcome drops the
/// operation — exactly the outcome the endpoint filter short-circuits. Enabled
/// capabilities (including flag-on experimental ones), unregistered ids, and the
/// wrong-edition (<see cref="CapabilityReasonCodes.LicenseRequired"/>) outcome all
/// stay in the document, mirroring the filter's pass-through set.
/// </para>
/// <para>
/// This is a behavioural no-op until an experimental capability is actually flipped
/// off-by-default (Track T10 / #2346): with no experimental descriptors gated, no
/// operation resolves experimental-disabled and the document is unchanged. It is
/// registered into the <c>AddOpenApi(...)</c> pipeline via
/// <c>OpenApiOptions.AddCapabilityGate()</c>.
/// </para>
/// </remarks>
internal sealed class CapabilityGateOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
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
        foreach (var entry in document.Paths)
        {
            if (string.Equals(NormalizeKey(entry.Key), target, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Key;
            }
        }

        return null;
    }

    private static string NormalizeKey(string? path)
        => string.IsNullOrEmpty(path) ? string.Empty : path.Trim('/');
}
