// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
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
    public void EveryCatalogEntry_IsImplementedOrExperimentalMaturity()
    {
        var catalog = LoadCommittedCatalog();

        catalog.SchemaVersion.Should().Be(FeatureCatalogGenerator.SchemaVersion);
        catalog.Entries.Should().NotBeEmpty();
        catalog.Entries.Select(entry => entry.Id)
            .Should().OnlyHaveUniqueItems("the catalog id is the canonical per-entry key");

        // T10 (#2346): the built-experimental route groups are flipped to the
        // `experimental` tier; every other test-backed route stays `implemented`.
        catalog.Entries.Should().OnlyContain(
            entry => entry.Maturity == FeatureCatalogGenerator.MaturityImplemented
                || entry.Maturity == FeatureCatalogGenerator.MaturityExperimental,
            "the catalog projects only implemented (in-release) or experimental "
            + "(built-experimental, gated-off) test-backed routes.");
    }

    [ArchitectureTest]
    public void ExperimentalEntries_AreExactlyTheGatedRouteGroups()
    {
        var catalog = LoadCommittedCatalog();

        // The catalog `experimental` tier must be exactly the routes the T5
        // endpoint gate 404s (the WithCapabilityGate route groups), no more and no
        // less — the route → descriptor map is the single source both project from.
        foreach (var entry in catalog.Entries)
        {
            var gatedDescriptorId = FeatureCatalogGenerator.ResolveDescriptorIdForRoute(entry.Route);
            if (gatedDescriptorId is null)
            {
                entry.Maturity.Should().Be(
                    FeatureCatalogGenerator.MaturityImplemented,
                    "the in-release route {0} {1} is not part of a flipped experimental group",
                    entry.Method,
                    entry.Route);
            }
            else
            {
                entry.Maturity.Should().Be(
                    FeatureCatalogGenerator.MaturityExperimental,
                    "the built-experimental route {0} {1} is gated by {2}",
                    entry.Method,
                    entry.Route,
                    gatedDescriptorId);
            }
        }

        // Sanity: at least one experimental entry exists for every flipped family so
        // a regression that silently stopped projecting `experimental` fails here.
        var experimentalRoutes = catalog.Entries
            .Where(entry => entry.Maturity == FeatureCatalogGenerator.MaturityExperimental)
            .Select(entry => entry.Route)
            .ToArray();

        // /api/v1/temporal/* was promoted to GA (Implemented) in #2429, and
        // /api/v1/admin/alerts/* in #2427 — neither is an experimental route group any
        // longer, so both are intentionally absent from this sanity set.
        experimentalRoutes.Should().Contain(route => route.StartsWith("/api/v1/admin/security/client-certificates", StringComparison.OrdinalIgnoreCase));
        experimentalRoutes.Should().Contain(route => route.Contains("/replicas", StringComparison.OrdinalIgnoreCase));
        // /api/v1/streaming/features* was promoted to GA (Implemented) in #2428 — no longer an
        // experimental route group, so it is intentionally absent from this sanity set.
        // Branch versioning (VMS REST surface) gated Preview in the BH6-001/BH6-002 fix batch.
        experimentalRoutes.Should().Contain(route => route.Contains("/VersionManagementServer", StringComparison.OrdinalIgnoreCase));
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
