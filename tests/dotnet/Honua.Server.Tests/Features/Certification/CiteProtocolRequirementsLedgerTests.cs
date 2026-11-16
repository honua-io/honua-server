// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Certification;

[Trait("Tier", "Fast")]
public sealed partial class CiteProtocolRequirementsLedgerTests
{
    private static readonly JsonDocument Ledger = JsonDocument.Parse(
        File.ReadAllBytes(RepositoryPaths.Resolve("certification", "cite-protocol-requirements.v1.json")));

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
