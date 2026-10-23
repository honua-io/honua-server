// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Server-side mirror of honua-release's docs gate <c>tools/check_ga_surface.py</c>
/// (honua-release#100, #379). honua-evidence ingests the committed
/// <c>docs/gis/data/capability-matrix.v1.json</c> from honua-server trunk, not from the
/// pinned release SHA, so any trunk commit that leaves a GA-advertised key under the proving-test
/// floor reddens the release train's <c>ga-surface</c> check with no server-side signal. This test
/// applies the same corpus and criteria to the committed artifact so the regression fails here,
/// on the PR that causes it.
/// </summary>
/// <remarks>
/// Corpus (identical to <c>advertised_ga_keys</c>): every capability whose <c>noSurface</c> is
/// absent/null/false and whose <c>maturity.implemented</c> is greater than zero. A key leaves the
/// corpus only by an honest demotion (a <c>capability-maturity-overrides.v1.json</c> row, a
/// registry maturity, or a no-surface annotation), never by lowering the floor here.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class GaSurfaceProvingTestFloorTests
{
    /// <summary>
    /// honua-release <c>docs/capabilities.yaml</c> <c>defaults.minProvingTests</c>. Raising it there
    /// requires raising it here; lowering it here alone does not move the release gate.
    /// </summary>
    private const int MinProvingTests = 5;

    [ArchitectureTest]
    public void EveryAdvertisedGaKey_MeetsTheReleaseGateEvidenceBar()
    {
        var advertised = LoadAdvertisedGaKeys();

        advertised.Should().NotBeEmpty(
            "a matrix with zero advertised-GA keys is treated as BLOCKED by the release gate, never a pass");

        var failures = new List<string>();
        foreach (var capability in advertised)
        {
            var key = capability.GetProperty("key").GetString()!;

            if (capability.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "internal")
            {
                failures.Add($"{key}: declared internal (ruling R29) but advertised as GA");
                continue;
            }

            var proving = capability.TryGetProperty("provingTestCount", out var count)
                && count.ValueKind == JsonValueKind.Number
                ? count.GetInt32()
                : 0;
            if (proving < MinProvingTests)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{key}: provingTestCount={proving} below floor {MinProvingTests}"));
            }

            if (capability.TryGetProperty("cite", out var cite) && cite.ValueKind == JsonValueKind.Array)
            {
                foreach (var suite in cite.EnumerateArray())
                {
                    var passRate = suite.TryGetProperty("passRate", out var rate)
                        && rate.ValueKind == JsonValueKind.Number
                        ? rate.GetDouble()
                        : 0d;
                    if (passRate < 100d)
                    {
                        failures.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{key}: CITE suite {suite.GetProperty("suite").GetString()} at {passRate}% (<100%)"));
                    }
                }
            }
        }

        failures.Should().BeEmpty(
            "every capability key the matrix advertises as GA (implemented > 0, surfaced) must carry at "
            + "least {0} proving tests and 100% on every joined CITE suite, or honua-release's docs gate "
            + "(tools/check_ga_surface.py) fails. Add real proving tests (feature-catalog proving_tests), "
            + "or demote the key with a reviewed capability-maturity-overrides.v1.json row citing the "
            + "decision, then regenerate the matrix.",
            MinProvingTests);
    }

    private static List<JsonElement> LoadAdvertisedGaKeys()
    {
        var path = Path.Join(ArchitectureTestHelpers.ResolveRepositoryRoot(),
            "docs", "gis", "data", "capability-matrix.v1.json");
        using var matrix = JsonDocument.Parse(File.ReadAllText(path));

        var advertised = new List<JsonElement>();
        foreach (var capability in matrix.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            if (capability.TryGetProperty("noSurface", out var noSurface)
                && noSurface.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined)
                && !(noSurface.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(noSurface.GetString())))
            {
                continue;
            }

            var implemented = capability.TryGetProperty("maturity", out var maturity)
                && maturity.ValueKind == JsonValueKind.Object
                && maturity.TryGetProperty("implemented", out var tier)
                && tier.ValueKind == JsonValueKind.Number
                ? tier.GetInt32()
                : 0;
            if (implemented > 0)
            {
                advertised.Add(capability.Clone());
            }
        }

        return advertised;
    }
}
