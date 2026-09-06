// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Certification;

/// <summary>
/// Keeps <c>certification/cite-protocol-requirements.v1.json</c> honest about which OGC CITE lanes
/// actually exist in this repository (honua-server#4425).
/// </summary>
/// <remarks>
/// <para>
/// The ledger declared <c>serve.ogc-api-records</c> (and EDR, Coverages, Maps, Styles,
/// SensorThings) as <c>maturity: "supported"</c> with a <c>canonical_client: "OGC CITE"</c>, a
/// named <c>client_lane</c> and <c>required_tier: "nightly"</c> — while no such workflow, Docker
/// composition or runner script existed. Section 14 of the quality contract requires that public
/// compliance claims equal the receipts, and a declared lane that does not exist must not be
/// counted as evidence.
/// </para>
/// <para>
/// Rather than freeze a hand-maintained list, this derives the truth from the repository: a lane is
/// implemented when its CITE suite has both a <c>docker/cite/&lt;suite&gt;</c> composition and a
/// <c>scripts/conformance/cite/run-cite-&lt;suite&gt;-tests.sh</c> runner. Building one of the
/// absent lanes therefore makes this test fail until the ledger is updated to claim it, and
/// deleting a runner makes it fail until the ledger stops claiming it.
/// </para>
/// </remarks>
[Trait("Tier", "Fast")]
public sealed class CiteProtocolRequirementsLedgerTests
{
    /// <summary>
    /// Maps a ledger <c>surface</c> to the CITE suite directory that certifies it. Surfaces absent
    /// from this map have no runner and must be declared absent in the ledger.
    /// </summary>
    private static readonly Dictionary<string, string> SuiteBySurface = new(StringComparer.Ordinal)
    {
        ["ogc-api-features"] = "ogc-api-features",
        ["ogc-api-features-1-0"] = "ogc-api-features",
        ["ogc-api-tiles"] = "ogc-api-tiles",
        ["ogc-api-tiles-1-0"] = "ogc-api-tiles",
        ["ogc-api-processes"] = "ogc-api-processes",
        ["wfs"] = "wfs20",
        ["wfs-1-0"] = "wfs10",
        ["wfs-1-1"] = "wfs11",
        ["wfs-2-0"] = "wfs20",
        ["wms"] = "wms13",
        ["wms-1-3"] = "wms13",
        ["wmts"] = "wmts10",
        ["wmts-1-0"] = "wmts10",
        ["wcs"] = "wcs20",
        ["wcs-2-0"] = "wcs20",
        // The per-operation umbrella rows are certified through the versioned lanes above; the
        // OGC API - Records operation is the exception and is declared absent in the ledger.
        ["ogc"] = "wfs20"
    };

    private static readonly JsonDocument Ledger = JsonDocument.Parse(
        File.ReadAllBytes(RepositoryPaths.Resolve("certification", "cite-protocol-requirements.v1.json")));

    private static IEnumerable<JsonElement> CiteRequirements
        => Ledger.RootElement.GetProperty("requirements").EnumerateArray()
            .Where(requirement => requirement.GetProperty("canonical_client").GetString() == "OGC CITE");

    [Fact]
    public void EveryCiteRequirement_DeclaresWhetherItsLaneIsImplemented()
    {
        foreach (var requirement in CiteRequirements)
        {
            var key = Describe(requirement);
            requirement.TryGetProperty("cite_lane_status", out var status).Should().BeTrue(
                $"{key} must declare whether its CITE lane exists");
            status.GetString().Should().BeOneOf(["implemented", "absent"], key);
        }
    }

    [Fact]
    public void EveryLaneDeclaredImplemented_HasARunnerAndACompositionInThisRepository()
    {
        foreach (var requirement in CiteRequirements.Where(IsImplemented))
        {
            var key = Describe(requirement);
            var surface = requirement.GetProperty("surface").GetString()!;

            SuiteBySurface.TryGetValue(surface, out var suite).Should().BeTrue(
                $"{key} claims an implemented CITE lane, so its surface must map to a CITE suite");

            Directory.Exists(RepositoryPaths.Resolve("docker", "cite", suite!)).Should().BeTrue(
                $"{key} claims an implemented lane, so docker/cite/{suite} must exist");
            File.Exists(RepositoryPaths.Resolve("scripts", "conformance", "cite", $"run-cite-{suite}-tests.sh"))
                .Should().BeTrue(
                    $"{key} claims an implemented lane, so its runner script must exist");
        }
    }

    [Fact]
    public void EveryLaneWithoutARunner_IsDeclaredAbsentWithAReason()
    {
        foreach (var requirement in CiteRequirements)
        {
            var key = Describe(requirement);
            var surface = requirement.GetProperty("surface").GetString()!;
            var hasRunner = SuiteBySurface.TryGetValue(surface, out var suite)
                            && Directory.Exists(RepositoryPaths.Resolve("docker", "cite", suite!));
            if (hasRunner && requirement.GetProperty("operation").GetString() != "OGC-OP-OGC-API-RECORDS-DISCOVERY")
            {
                continue;
            }

            IsImplemented(requirement).Should().BeFalse(
                $"{key} has no CITE runner in this repository, so the ledger must not claim one — " +
                "a declared lane that does not exist must not be counted as evidence");
            requirement.GetProperty("cite_lane_absence_reason").GetString().Should().NotBeNullOrWhiteSpace(
                $"{key} must record why its declared lane is absent");
        }
    }

    [Fact]
    public void AbsentLanes_StillDeclareTheLaneNameSoTheGapIsAddressable()
    {
        var absent = CiteRequirements.Where(requirement => !IsImplemented(requirement)).ToArray();

        absent.Should().NotBeEmpty(
            "the ledger currently declares nightly CITE lanes for surfaces with no runner; if that " +
            "ever stops being true this test should be deleted along with the absent rows");
        foreach (var requirement in absent)
        {
            requirement.GetProperty("client_lane").GetString().Should().NotBeNullOrWhiteSpace(
                "an absent lane keeps its intended name so the work item stays identifiable");
        }
    }

    private static bool IsImplemented(JsonElement requirement)
        => requirement.TryGetProperty("cite_lane_status", out var status)
           && status.GetString() == "implemented";

    private static string Describe(JsonElement requirement)
        => $"{requirement.GetProperty("capability_key").GetString()}"
           + $" / {requirement.GetProperty("operation").GetString()}"
           + $" (lane {requirement.GetProperty("client_lane").GetString()})";
}
