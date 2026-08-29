// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the evidence-correctness repairs for the analyst-client baselines introduced by #3481.
/// </summary>
public sealed class ClientCertificationEvidenceCorrectnessTests
{
    private const string ProducingServerCommit = "dbb368c5d06fb744e5289e4fd6ab30acc3973698";

    private static readonly HashSet<string> AnalystLanes =
    [
        "duckdb", "py-geopandas", "py-owslib", "py-pystac", "r-sf",
    ];

    private static readonly HashSet<string> FrozenGapIds =
    [
        "multipart-geometry-absent",
        "polygon-hole-geometry-absent",
        "unicode-attribute-values-absent",
        "line-and-polygon-not-on-canonical-service",
        "edit-path-uncertified",
        "raster-absent-on-canonical-service",
        "style-resources-absent",
        "portal-map-and-dashboard-items-absent",
        "odata-surface-uses-separate-seed",
        "active-lanes-bind-render-fixture-not-canonical-vector",
        "js-lane-identity-not-pinned",
        "receipt-binding-uses-per-file-digests",
        "auth-policy-revision-not-in-receipts",
        "active-baselines-lack-receipt-bindings",
        "expired-credential-unrealized",
        "insufficient-role-assertion-absent",
        "cross-tenant-denial-unrealized",
        "proposer-approver-unrealized",
        "licensed-entitlement-unrealized-locally",
        "tls-not-exercised-in-local-profile",
        "runtime-composition-not-content-addressed",
    ];

    [ArchitectureTest]
    public void AnalystBaselines_BindAnExactProducingServerCommit()
    {
        foreach (var (_, document) in ReadAnalystBaselines())
        {
            using (document)
            {
                var commit = document.RootElement.GetProperty("server_commit").GetString();
                commit.Should().MatchRegex("^[0-9a-f]{40}$");
                commit.Should().NotBe("unknown");
                commit.Should().Be(ProducingServerCommit,
                    "#3481's baselines were produced from the original certification run commit");
            }
        }
    }

    [ArchitectureTest]
    public void GeoPandasHttpxObservations_NameTheClientThatPerformedEachCall()
    {
        foreach (var (path, document) in ReadAnalystBaselines())
        {
            using (document)
            {
                var lane = document.RootElement.GetProperty("client_lane").GetString();
                if (lane != "py-geopandas")
                {
                    continue;
                }

                foreach (var result in ResultsAndExtensions(document.RootElement))
                {
                    if (result.GetProperty("test_case_id").GetString() is
                        "CERT-AUTH-01" or "CERT-AUTH-02" or "NB-GPD-AUTH-01")
                    {
                        result.GetProperty("client_identity").GetString().Should().Be("httpx",
                            $"GeoPandas has no control-plane API and did not perform the auth probe in {path}");
                    }
                }
            }
        }
    }

    [ArchitectureTest]
    public void GeoPandasHttpBaselines_DoNotClaimTlsPassed()
    {
        foreach (var (_, document) in ReadAnalystBaselines().Where(entry =>
                     entry.Document.RootElement.GetProperty("client_lane").GetString() == "py-geopandas"))
        {
            using (document)
            {
                var tls = document.RootElement.GetProperty("results").EnumerateArray()
                    .Single(result => result.GetProperty("test_case_id").GetString() == "CERT-CONN-02");
                tls.GetProperty("status").GetString().Should().Be("skip");
                tls.GetProperty("notes").GetString().Should().Contain("plain HTTP");
            }
        }
    }

    [ArchitectureTest]
    public void FrozenFixture_PreservesAllTwentyOneGovernedGaps()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(
            root, "docs", "gis", "data", "client-certification-fixture.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var actual = document.RootElement.GetProperty("gaps").EnumerateArray()
            .Select(gap => gap.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        actual.Should().BeEquivalentTo(FrozenGapIds,
            "evidence corrections must not fabricate coverage for any frozen fixture gap");
    }

    private static IEnumerable<(string Path, JsonDocument Document)> ReadAnalystBaselines()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var evidenceRoot = ArchitectureTestHelpers.CombinePath(root, "tests", "baselines", "client-compat");
        foreach (var path in Directory.EnumerateFiles(evidenceRoot, "*.cert.json", SearchOption.AllDirectories))
        {
            var document = JsonDocument.Parse(File.ReadAllText(path));
            if (AnalystLanes.Contains(document.RootElement.GetProperty("client_lane").GetString()!))
            {
                yield return (path, document);
            }
            else
            {
                document.Dispose();
            }
        }
    }

    private static IEnumerable<JsonElement> ResultsAndExtensions(JsonElement root)
        => root.GetProperty("results").EnumerateArray()
            .Concat(root.GetProperty("extensions").EnumerateArray());
}
