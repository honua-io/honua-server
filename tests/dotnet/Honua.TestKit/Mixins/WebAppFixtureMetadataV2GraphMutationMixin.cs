// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the Metadata v2 graph mutation helpers that <see cref="WebAppFixture"/>
/// historically inlined as ~400 LOC of <c>UpdateV2Xxx</c>, <c>SetV2LayerEnabled</c>, and
/// <c>AddAdminSampleMetadataV2Graph</c> methods. The mutation helpers all run the same
/// pattern: read the current snapshot from a <see cref="TestMetadataV2GraphProvider"/>,
/// transform resources / services / publications, bump the revision, and write back. They
/// are stateless w.r.t. the fixture itself — they only need the provider — so they live as
/// static helpers on this mixin and the fixture exposes thin delegating wrappers to keep
/// the public API surface unchanged.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), this is the second slice of the WebAppFixture
/// mixin split. Behaviour is byte-identical to the previous inline implementations — this
/// is a pure relocation.
/// </remarks>
internal static class WebAppFixtureMetadataV2GraphMutationMixin
{
    private const string ProviderNotRegisteredMessage =
        "Test V2 graph provider not registered as TestMetadataV2GraphProvider.";

    /// <summary>
    /// Resolves the singleton test V2 graph provider from the fixture's service scope or
    /// throws if the test pipeline replaced it with an incompatible implementation. All
    /// mutation helpers in this mixin go through this accessor so the error path is
    /// shared.
    /// </summary>
    internal static TestMetadataV2GraphProvider RequireProvider(WebAppFixture fixture)
    {
        return fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException(ProviderNotRegisteredMessage);
    }

    /// <summary>
    /// Updates the canonical resource published at <paramref name="layerIndex"/> with any
    /// of the supplied metadata slots. <see cref="WebAppFixture.UpdateV2ResourceMetadata"/>
    /// is a thin wrapper over this helper. Clearing flags take precedence over value
    /// arguments; omitting both leaves a slot untouched.
    /// </summary>
    internal static void UpdateResourceMetadata(
        WebAppFixture fixture,
        int layerIndex,
        AccessPolicy? accessPolicy,
        MetadataV2ResourceTemporal? temporal,
        MetadataV2ResourceSpatial? spatial,
        MetadataV2PermanentFilter? permanentFilter,
        MetadataV2ExtrusionInfo? extrusion,
        JsonElement? stacExtension,
        bool clearAccessPolicy,
        bool clearTemporal,
        bool clearPermanentFilter,
        bool clearExtrusion)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            var next = resources[i];
            if (clearAccessPolicy)
            {
                next = next with { AccessPolicy = null };
            }
            else if (accessPolicy is not null)
            {
                next = next with { AccessPolicy = accessPolicy };
            }

            if (clearTemporal)
            {
                next = next with { Temporal = null };
            }
            else if (temporal is not null)
            {
                next = next with { Temporal = temporal };
            }

            if (spatial is not null)
            {
                next = next with { Spatial = spatial };
            }

            if (clearPermanentFilter)
            {
                next = next with { PermanentFilter = null };
            }
            else if (permanentFilter is not null)
            {
                next = next with { PermanentFilter = permanentFilter };
            }

            if (clearExtrusion)
            {
                next = next with { Extrusion = null };
            }
            else if (extrusion is not null)
            {
                next = next with { Extrusion = extrusion };
            }

            if (stacExtension.HasValue)
            {
                var extensions = new Dictionary<string, JsonElement>(next.Extensions, StringComparer.Ordinal)
                {
                    ["stac"] = stacExtension.Value,
                };
                next = next with { Extensions = extensions };
            }

            resources[i] = next;
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// Adds or replaces a Metadata v2 schema field on the resource published at
    /// <paramref name="layerIndex"/>. Match on field name is case-insensitive so callers
    /// can re-assert a field with the same logical name.
    /// </summary>
    internal static void UpdateResourceSchemaField(WebAppFixture fixture, int layerIndex, MetadataV2Field field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = resources[i].SchemaFields
                .Where(existing => !string.Equals(existing.Name, field.Name, StringComparison.OrdinalIgnoreCase))
                .Append(field)
                .ToArray();
            resources[i] = resources[i] with { SchemaFields = fields };
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// Sets (or clears) the Esri subtype set on the resource published at
    /// <paramref name="layerIndex"/> so FeatureServer layer-metadata serving of
    /// <c>subtypeField</c> / <c>subtypes</c> / <c>defaultSubtypeCode</c> can be exercised
    /// end-to-end against the served graph (honua-server#1378).
    /// </summary>
    internal static void UpdateResourceSubtypes(WebAppFixture fixture, int layerIndex, MetadataV2Subtypes? subtypes)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            resources[i] = resources[i] with { Subtypes = subtypes };
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// Renames the resource bound to the publication with <paramref name="layerIndex"/>.
    /// Mirrors the v1 layer-rename behaviour CITE-conformance tests depend on.
    /// </summary>
    internal static void UpdateResourceName(WebAppFixture fixture, int layerIndex, string newName)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            resources[i] = resources[i] with
            {
                Metadata = resources[i].Metadata with { Name = newName }
            };
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// Updates every service that shares the supplied logical name (case-insensitive).
    /// The test graph fans out one service per protocol cluster under the same name, so
    /// matches sweep across all of them.
    /// </summary>
    internal static void UpdateServiceMetadata(
        WebAppFixture fixture,
        string serviceName,
        IReadOnlyList<string>? enabledProtocols,
        AccessPolicy? accessPolicy,
        bool clearAccessPolicy)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var services = snapshot.Graph.Services.ToArray();
        var mutated = false;
        for (var i = 0; i < services.Length; i++)
        {
            if (!string.Equals(services[i].Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var next = services[i];
            if (enabledProtocols is not null)
            {
                next = next with { Protocols = enabledProtocols };
            }
            if (clearAccessPolicy)
            {
                next = next with { AccessPolicy = null };
            }
            else if (accessPolicy is not null)
            {
                next = next with { AccessPolicy = accessPolicy };
            }

            services[i] = next;
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Services = services,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// Flips Active↔Retired on the publications + resources bound to the supplied layer
    /// index. The operational state stays Ready in both directions so visibility tests
    /// observe the lifecycle change in isolation.
    /// </summary>
    internal static void SetLayerEnabled(WebAppFixture fixture, int layerIndex, bool enabled)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var affectedResourceIds = snapshot.Graph.Publications
            .Where(publication => publication.LayerIndex == layerIndex)
            .Select(publication => publication.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (affectedResourceIds.Count == 0)
        {
            return;
        }

        var status = enabled
            ? new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
            }
            : new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Retired,
                State = MetadataV2OperationalState.Ready,
            };

        var resources = snapshot.Graph.Resources
            .Select(resource => affectedResourceIds.Contains(resource.Metadata.Id)
                ? resource with { Status = status }
                : resource)
            .ToArray();
        var publications = snapshot.Graph.Publications
            .Select(publication => publication.LayerIndex == layerIndex
                ? publication with { Status = status }
                : publication)
            .ToArray();

        provider.SetGraph(snapshot.Graph with
        {
            Resources = resources,
            Publications = publications,
            Revision = snapshot.Graph.Revision + 1,
        });
    }

    /// <summary>
    /// Merges the admin-sample subgraph from
    /// <see cref="WebAppFixtureMetadataV2Mixin.BuildAdminSampleMetadataV2Graph"/> into
    /// the live test graph. Existing nodes with matching ids are replaced so the helper
    /// is idempotent across re-applies.
    /// </summary>
    internal static void AddAdminSample(WebAppFixture fixture)
    {
        var provider = RequireProvider(fixture);

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var adminGraph = WebAppFixtureMetadataV2Mixin.BuildAdminSampleMetadataV2Graph();
        var adminResourceIds = adminGraph.Resources
            .Select(static resource => resource.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var adminBindingIds = adminGraph.StorageBindings
            .Select(static binding => binding.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var adminServiceIds = adminGraph.Services
            .Select(static service => service.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var adminPublicationIds = adminGraph.Publications
            .Select(static publication => publication.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);

        var graph = snapshot.Graph;
        provider.SetGraph(graph with
        {
            Resources = graph.Resources
                .Where(resource => !adminResourceIds.Contains(resource.Metadata.Id))
                .Concat(adminGraph.Resources)
                .ToArray(),
            StorageBindings = graph.StorageBindings
                .Where(binding => !adminBindingIds.Contains(binding.Metadata.Id))
                .Concat(adminGraph.StorageBindings)
                .ToArray(),
            Services = graph.Services
                .Where(service => !adminServiceIds.Contains(service.Metadata.Id))
                .Concat(adminGraph.Services)
                .ToArray(),
            Publications = graph.Publications
                .Where(publication => !adminPublicationIds.Contains(publication.Metadata.Id))
                .Concat(adminGraph.Publications)
                .ToArray(),
            Revision = graph.Revision + 1,
        });
    }

    /// <summary>
    /// Swaps in the seed-specific V2 graph when the fixture has been told to apply
    /// <c>tests/seed/odata.yaml</c> or <c>tests/seed/spatial-reference.yaml</c>. No-op for
    /// any other seed (the default graph already covers it). Returns silently when no seed
    /// path is configured so callers can fire-and-forget from <c>InitializeAsync</c>.
    /// </summary>
    internal static void ApplySeedSpecificGraph(WebAppFixture fixture, string? seedPath)
    {
        if (seedPath is null)
        {
            return;
        }

        var provider = RequireProvider(fixture);

        var seedFile = Path.GetFileName(seedPath);
        if (seedFile.Equals("odata.yaml", StringComparison.OrdinalIgnoreCase))
        {
            provider.SetGraph(WebAppFixtureMetadataV2Mixin.BuildODataSeedTestGraph());
            return;
        }

        if (seedFile.Equals("spatial-reference.yaml", StringComparison.OrdinalIgnoreCase))
        {
            provider.SetGraph(WebAppFixtureMetadataV2Mixin.BuildSpatialReferenceSeedTestGraph());
        }
    }
}
