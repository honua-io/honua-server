// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Certification;

/// <summary>
/// Keeps <c>certification/cite-protocol-requirements.v1.json</c> honest about which OGC CITE lanes
/// can actually produce a receipt (honua-server#4425).
/// </summary>
/// <remarks>
/// <para>
/// The ledger declared <c>serve.ogc-api-records</c> (and EDR, Coverages, Maps, Styles,
/// SensorThings) as <c>maturity: "supported"</c> with a <c>canonical_client: "OGC CITE"</c>, a
/// named <c>client_lane</c> and <c>required_tier: "nightly"</c> — while no such workflow, Docker
/// composition or runner existed. Section 14 of the quality contract requires that public
/// compliance claims equal the receipts, and a declared lane that does not exist must not be
/// counted as evidence.
/// </para>
/// <para>
/// The authority for "can this row produce a receipt" is not a hand-maintained list: it is
/// <c>SUITE_BY_SURFACE</c> in
/// <c>scripts/conformance/cite/build_protocol_certification_fragment.py</c>, the map the fragment
/// builder actually consults. A surface missing from it is emitted as a <c>skip</c> with
/// "No current CITE suite maps truthfully to this governed operation", no matter what compositions
/// exist on disk. This test parses that map out of the builder and requires the ledger to agree, so
/// wiring a new lane fails here until the ledger claims it, and dropping one fails here until the
/// ledger stops.
/// </para>
/// </remarks>
[Trait("Tier", "Fast")]
public sealed partial class CiteProtocolRequirementsLedgerTests
{
    private static readonly JsonDocument Ledger = JsonDocument.Parse(
        File.ReadAllBytes(RepositoryPaths.Resolve("certification", "cite-protocol-requirements.v1.json")));

    /// <summary>Surfaces the certification fragment builder can map onto a CITE suite.</summary>
    private static readonly HashSet<string> ExecutableSurfaces = ReadExecutableSurfaces();

    private static IEnumerable<JsonElement> CiteRequirements
        => Ledger.RootElement.GetProperty("requirements").EnumerateArray()
            .Where(requirement => requirement.GetProperty("canonical_client").GetString() == "OGC CITE");

    [Fact]
    public void TheFragmentBuilderMap_IsParseable()
    {
        // Guards the parser itself: if the builder's map is reformatted beyond recognition the
        // other assertions would silently degrade into "nothing is executable".
        ExecutableSurfaces.Should().NotBeEmpty(
            "SUITE_BY_SURFACE must be readable out of build_protocol_certification_fragment.py");
        ExecutableSurfaces.Should().Contain("ogc-api-features-1-0");
    }

    [Fact]
    public void EveryCiteRequirement_DeclaresWhetherItsLaneCanProduceAReceipt()
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
    public void LaneStatus_MatchesWhatTheFragmentBuilderCanActuallyExecute()
    {
        foreach (var requirement in CiteRequirements)
        {
            var key = Describe(requirement);
            var surface = requirement.GetProperty("surface").GetString()!;
            var executable = ExecutableSurfaces.Contains(surface);

            IsImplemented(requirement).Should().Be(
                executable,
                executable
                    ? $"{key} maps to a CITE suite in the fragment builder, so the ledger may claim it"
                    : $"{key} has no entry in the fragment builder's SUITE_BY_SURFACE, so every run " +
                      "emits a skip and the ledger must not claim a lane — a declared lane that " +
                      "cannot produce a receipt must not be counted as evidence");
        }
    }

    [Fact]
    public void EveryAbsentLane_RecordsWhyAndKeepsItsIntendedLaneName()
    {
        var absent = CiteRequirements.Where(requirement => !IsImplemented(requirement)).ToArray();

        absent.Should().NotBeEmpty(
            "the ledger currently declares nightly CITE lanes for surfaces the fragment builder " +
            "cannot execute; if that ever stops being true, delete this test with the last absent row");
        foreach (var requirement in absent)
        {
            var key = Describe(requirement);
            requirement.GetProperty("cite_lane_absence_reason").GetString().Should().NotBeNullOrWhiteSpace(
                $"{key} must record why its declared lane produces no receipt");
            requirement.GetProperty("client_lane").GetString().Should().NotBeNullOrWhiteSpace(
                $"{key} keeps its intended lane name so the work item stays identifiable");
        }
    }

    [Fact]
    public void EveryImplementedLane_RecordsNoAbsenceReason()
    {
        foreach (var requirement in CiteRequirements.Where(IsImplemented))
        {
            requirement.GetProperty("cite_lane_absence_reason").ValueKind.Should().Be(
                JsonValueKind.Null,
                $"{Describe(requirement)} claims an implemented lane, so it must carry no absence reason");
        }
    }

    /// <summary>
    /// Parses <c>SUITE_BY_SURFACE = { "surface": "suite", ... }</c> out of the fragment builder.
    /// </summary>
    private static HashSet<string> ReadExecutableSurfaces()
    {
        var builder = File.ReadAllText(RepositoryPaths.Resolve(
            "scripts", "conformance", "cite", "build_protocol_certification_fragment.py"));
        var block = MapBlockPattern().Match(builder);
        if (!block.Success)
        {
            return [];
        }

        return [.. MapEntryPattern().Matches(block.Groups["body"].Value)
            .Select(match => match.Groups["surface"].Value)];
    }

    private static bool IsImplemented(JsonElement requirement)
        => requirement.TryGetProperty("cite_lane_status", out var status)
           && status.GetString() == "implemented";

    private static string Describe(JsonElement requirement)
        => $"{requirement.GetProperty("capability_key").GetString()}"
           + $" / {requirement.GetProperty("operation").GetString()}"
           + $" (lane {requirement.GetProperty("client_lane").GetString()})";

    [GeneratedRegex(@"SUITE_BY_SURFACE\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex MapBlockPattern();

    [GeneratedRegex("\"(?<surface>[^\"]+)\"\\s*:\\s*\"[^\"]+\"")]
    private static partial Regex MapEntryPattern();
}
