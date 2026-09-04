// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Drift guard for the evidence-based feature catalog (#1946, ADR-0054). The
/// catalog (<c>docs/gis/data/feature-catalog.json</c>) is a generated projection
/// of the shipped API surface, and this guard makes drift a red build:
/// <list type="number">
///   <item><description>
///     every <see cref="EndpointRegistry.All"/> route has a catalog entry;
///   </description></item>
///   <item><description>
///     every catalog entry resolves to a real registered route and carries at
///     least one <c>[Endpoint]</c>-attributed proving test;
///   </description></item>
///   <item><description>
///     the committed artifact equals freshly-generated output — so a hand-edit,
///     or a new endpoint added without regenerating, fails the build.
///   </description></item>
/// </list>
/// It mirrors the proof-ledger governance tests in
/// <see cref="PublicInterfaceProofLedgerTests"/> and reuses the same
/// endpoint↔test↔proof-ledger discovery helpers.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class FeatureCatalogDriftTests
{
    [ArchitectureTest]
    public void EveryEndpointRegistryRoute_HasACatalogEntry()
    {
        var catalog = LoadCommittedCatalog();
        var cataloged = catalog.Entries
            .Select(entry => EndpointKey.Format(entry.Method, entry.Route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = EndpointRegistry.All
            .Select(endpoint => EndpointKey.Format(endpoint.Method, endpoint.Path))
            .Where(key => !cataloged.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "every shipped endpoint in EndpointRegistry.All must have a feature-catalog entry; "
            + "regenerate with scripts/generate-feature-catalog.sh after adding a route.");
    }

    [ArchitectureTest]
    public void EveryCatalogEntry_ResolvesToARegisteredRoute()
    {
        var catalog = LoadCommittedCatalog();
        var registered = EndpointRegistry.All
            .Select(endpoint => EndpointKey.Format(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = catalog.Entries
            .Select(entry => EndpointKey.Format(entry.Method, entry.Route))
            .Where(key => !registered.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        orphans.Should().BeEmpty(
            "every feature-catalog entry must resolve to a route registered in EndpointRegistry.All; "
            + "a stale entry indicates a hand-edit or a removed endpoint.");
    }

    [ArchitectureTest]
    public void EveryCatalogEntry_HasAtLeastOneProvingTest()
    {
        var catalog = LoadCommittedCatalog();

        var unproven = catalog.Entries
            .Where(entry => entry.ProvingTests.Length == 0)
            .Select(entry => EndpointKey.Format(entry.Method, entry.Route))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        unproven.Should().BeEmpty(
            "every implemented feature-catalog entry must be backed by at least one "
            + "[Endpoint]-attributed integration test (the capability→proving-test link); "
            + "an entry without a proving test means the API surface coverage gate would also fail.");
    }

    [ArchitectureTest]
    public void EveryCatalogEntry_CarriesAnExplainedMaturityTier()
    {
        var catalog = LoadCommittedCatalog();

        catalog.SchemaVersion.Should().Be(FeatureCatalogGenerator.SchemaVersion);
        catalog.Entries.Should().NotBeEmpty();
        catalog.Entries.Select(entry => entry.Id)
            .Should().OnlyHaveUniqueItems("the catalog id is the canonical per-entry key");

        // T10 (#2346): the built-experimental route groups are flipped to the
        // `experimental` tier; every other test-backed route stays `implemented` —
        // UNLESS a reviewed capability-maturity-overrides.v1.json row demotes it
        // (honua-release#100). Any other tier is unexplained and fails here.
        var overriddenCapabilities = CapabilityMaturityOverrides.Load().Overrides
            .ToDictionary(row => row.Capability, row => row.Maturity, StringComparer.Ordinal);

        var unexplained = catalog.Entries
            .Where(entry => entry.Maturity != FeatureCatalogGenerator.MaturityImplemented
                && entry.Maturity != FeatureCatalogGenerator.MaturityExperimental
                && entry.Maturity != FeatureCatalogGenerator.MaturityPreview)
            .Where(entry => !(overriddenCapabilities.TryGetValue(entry.Capability, out var demoted)
                && string.Equals(demoted, entry.Maturity, StringComparison.Ordinal)))
            .Select(entry => $"{EndpointKey.Format(entry.Method, entry.Route)} -> {entry.Maturity}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        unexplained.Should().BeEmpty(
            "the catalog projects implemented (in-release) or experimental (built-experimental, "
            + "gated-off) test-backed routes, plus whatever tier a reviewed "
            + "capability-maturity-overrides.v1.json row demotes a capability to.");
    }

    [ArchitectureTest]
    public void ExperimentalEntries_AreExactlyTheGatedRouteGroups()
    {
        var catalog = LoadCommittedCatalog();

        // The catalog `experimental` tier must be exactly the routes the T5
        // endpoint gate 404s (the WithCapabilityGate route groups), no more and no
        // less — the route → descriptor map is the single source both project from.
        // A capability carrying a reviewed capability-maturity-overrides.v1.json row
        // (honua-release#100) is re-derived the same way the generator does: the LOWER
        // of the gate-derived tier and the override tier, so an override can shift an
        // entry off `implemented` but can never move a gated route ONTO it.
        var overrides = CapabilityMaturityOverrides.Load();

        foreach (var entry in catalog.Entries)
        {
            var gatedDescriptorId = FeatureCatalogGenerator.ResolveDescriptorIdForRoute(entry.Route);
            var descriptor = gatedDescriptorId is null ? null : new CapabilityRegistry().Find(gatedDescriptorId);
            var gateTier = descriptor?.Maturity switch
            {
                CapabilityMaturity.Experimental => FeatureCatalogGenerator.MaturityExperimental,
                CapabilityMaturity.Preview => FeatureCatalogGenerator.MaturityPreview,
                _ => FeatureCatalogGenerator.MaturityImplemented,
            };
            var expected = overrides.ResolveEffective(entry.Capability, gateTier);

            if (string.Equals(expected, gateTier, StringComparison.Ordinal))
            {
                entry.Maturity.Should().Be(
                    gateTier,
                    gatedDescriptorId is null
                        ? "the in-release route {0} {1} is not part of a flipped experimental group and carries no maturity override"
                        : "the built-experimental route {0} {1} is gated by {2}",
                    entry.Method,
                    entry.Route,
                    gatedDescriptorId);
            }
            else
            {
                entry.Maturity.Should().Be(
                    expected,
                    "the route {0} {1} is demoted to '{2}' by a reviewed capability-maturity-overrides.v1.json "
                    + "row for capability '{3}'",
                    entry.Method,
                    entry.Route,
                    expected,
                    entry.Capability);
            }
        }

        // Sanity: at least one experimental entry exists for every flipped family so
        // a regression that silently stopped projecting `experimental` fails here.
        var experimentalRoutes = catalog.Entries
            .Where(entry => entry.Maturity == FeatureCatalogGenerator.MaturityExperimental)
            .Select(entry => entry.Route)
            .ToArray();

        // /api/v1/temporal/* was promoted to GA and intentionally remains absent from this set.
        // /api/v1/admin/alerts/* is opt-in Preview and is therefore not part of the default
        // advertised surface even though its maturity is preview rather than experimental.
        // /api/v1/admin/security/client-certificates/* was promoted to GA in #2431 but DEMOTED
        // back to experimental in #2958, so it IS a flipped experimental route group today.
        experimentalRoutes.Should().Contain(route => route.StartsWith("/api/v1/admin/security/client-certificates", StringComparison.OrdinalIgnoreCase));
        // /api/v1/admin/services/{serviceId}/replicas* is not part of the FeatureServer sync
        // route family; the gated FeatureServer routes are covered by the preview set below.
        // /api/v1/streaming/features* remains Preview, so it is intentionally absent here.
        // Branch versioning (VMS REST surface) gated Preview in the BH6-001/BH6-002 fix batch.

        var previewRoutes = catalog.Entries
            .Where(entry => entry.Maturity == FeatureCatalogGenerator.MaturityPreview)
            .Select(entry => entry.Route)
            .ToArray();
        previewRoutes.Should().Contain(route => route.StartsWith("/api/v1/admin/alerts", StringComparison.OrdinalIgnoreCase));
        previewRoutes.Should().Contain(route => route.StartsWith("/api/v1/streaming/features", StringComparison.OrdinalIgnoreCase));
        previewRoutes.Should().Contain(route => route.StartsWith("/api/v1/admin/streaming/features", StringComparison.OrdinalIgnoreCase));
        previewRoutes.Should().Contain(route => route.StartsWith("/sta/v1.1", StringComparison.OrdinalIgnoreCase));
        experimentalRoutes.Should().Contain(route => route.Contains("/VersionManagementServer", StringComparison.OrdinalIgnoreCase));
    }

    [ArchitectureTest]
    public void GatedProductionRouteFamilies_AreProjectedAsNonGa()
    {
        FeatureCatalogGenerator.ResolveDescriptorIdForRoute("/api/v1/admin/alerts/rules")
            .Should().Be("alerts.geofence");
        FeatureCatalogGenerator.ResolveDescriptorIdForRoute(
                "/rest/services/{serviceId}/FeatureServer/createReplica")
            .Should().Be("sync.offline");
        FeatureCatalogGenerator.ResolveDescriptorIdForRoute("/api/v1/admin/scenes/ingest/citygml")
            .Should().Be("scene.bim-ingest");
        FeatureCatalogGenerator.ResolveDescriptorIdForRoute("/scenes/{sceneId}/tileset.json")
            .Should().Be("serve.3d-tiles-scene");
    }

    [ArchitectureTest]
    public void CommittedCatalog_EqualsFreshlyGeneratedOutput()
    {
        var committed = File.ReadAllText(FeatureCatalogPaths.CommittedArtifactPath());
        var generated = FeatureCatalogGenerator.Serialize(FeatureCatalogGenerator.Generate());

        committed.Should().Be(
            generated,
            "the committed feature-catalog.json must equal freshly-generated output; "
            + "a difference means the catalog was hand-edited or an endpoint/test changed "
            + "without regenerating. Run scripts/generate-feature-catalog.sh and commit the result.");
    }

    private static FeatureCatalog LoadCommittedCatalog()
    {
        var path = FeatureCatalogPaths.CommittedArtifactPath();
        File.Exists(path).Should().BeTrue($"the committed feature catalog must exist at {FeatureCatalogPaths.RelativePath}");

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, FeatureCatalogJsonContext.Default.FeatureCatalog)
            ?? throw new InvalidOperationException("Unable to deserialize feature-catalog.json.");
    }
}
