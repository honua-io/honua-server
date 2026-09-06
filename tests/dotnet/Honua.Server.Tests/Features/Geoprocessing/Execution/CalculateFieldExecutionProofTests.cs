// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Execution-content proof for <c>data-management.calculate-field</c> (#3937).
///
/// The prior evidence asserted only <c>success</c> and the presence of an updated
/// count; it never read the changed values back, so an implementation that
/// reported a count and wrote nothing (or wrote the wrong rows) passed. These
/// cases execute the owning FeatureServer <c>calculate</c> path against a real
/// published PostGIS layer with literal seeded rows, then read every row back
/// through the canonical protocol query route and assert exact post-state.
/// The oracle is the committed fixture plus arithmetic done here, never a
/// snapshot of what calculate produced.
/// </summary>
[Collection("Database")]
[Trait("Category", "CalculateFieldExecutionProof")]
public sealed class CalculateFieldExecutionProofTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private string _serviceName = null!;
    private int _routeLayerId;

    // The committed fixture. Every expectation below is derived from these
    // literals, not from a prior run's output.
    private static readonly (long Id, string Label, int Score, string? Note)[] SeedRows =
    [
        (1, "alpha", 10, "keep"),
        (2, "beta", 20, "keep"),
        (3, "gamma", 30, null),
        (4, "delta", 40, "keep"),
    ];

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var schema = _fixture.CurrentSchema!;
        _serviceName = "calcproof_" + Guid.NewGuid().ToString("N");

        await using (var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand($"""
                CREATE TABLE "{schema}".calcproof (
                    id bigint PRIMARY KEY,
                    label varchar(32) NOT NULL,
                    score integer NOT NULL,
                    note text,
                    geom geometry(Point,4326) NOT NULL);
                INSERT INTO "{schema}".calcproof VALUES
                    (1, 'alpha', 10, 'keep',  ST_SetSRID(ST_MakePoint(1, 1), 4326)),
                    (2, 'beta',  20, 'keep',  ST_SetSRID(ST_MakePoint(2, 2), 4326)),
                    (3, 'gamma', 30, NULL,    ST_SetSRID(ST_MakePoint(3, 3), 4326)),
                    (4, 'delta', 40, 'keep',  ST_SetSRID(ST_MakePoint(4, 4), 4326));
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        await _fixture.GetService<ILayerPublishingService>().PublishLayerAsync(
            new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
            {
                SearchPath = schema + ",public"
            }.ConnectionString,
            new LayerPublishRequest
            {
                Schema = schema,
                Table = "calcproof",
                LayerName = "Calculate proof layer",
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Srid = 4326,
                Fields = ["id", "label", "score", "note"],
                ServiceName = _serviceName,
                Enabled = true
            });

        _routeLayerId = await ResolveRouteLayerIdAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task Calculate_FilteredRows_WritesExactValuesAndLeavesExcludedRowsUntouched()
    {
        // score * 2 + 1 and UPPER(label) || '-C' over the rows with score >= 20.
        var response = await PostCalculateAsync(
            where: "score >= 20",
            calcExpression: """
                [{"field":"score","sqlExpression":"score * 2 + 1"},
                 {"field":"label","sqlExpression":"UPPER(label) || '-C'"}]
                """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(
            3, "exactly the three rows with score >= 20 match the filter");

        // Independent expectation from the committed fixture: the selected rows
        // carry the computed values, the excluded row is byte-identical to seed.
        await AssertRowsAsync(
        [
            (1, "alpha", 10, "keep"),
            (2, "BETA-C", 41, "keep"),
            (3, "GAMMA-C", 61, null),
            (4, "DELTA-C", 81, "keep"),
        ]);
    }

    [IntegrationTest]
    public async Task Calculate_ObjectIdSelection_UpdatesOnlyTheNamedFeature()
    {
        var response = await PostCalculateAsync(
            where: "id = 3",
            calcExpression: """[{"field":"note","value":"recalculated"}]""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(1);

        await AssertRowsAsync(
        [
            (1, "alpha", 10, "keep"),
            (2, "beta", 20, "keep"),
            (3, "gamma", 30, "recalculated"),
            (4, "delta", 40, "keep"),
        ]);
    }

    [IntegrationTest]
    public async Task Calculate_UnsupportedExpression_IsRejectedAndChangesNothing()
    {
        var response = await PostCalculateAsync(
            where: "1=1",
            calcExpression: """[{"field":"score","sqlExpression":"(SELECT score FROM calcproof)"}]""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a subquery is outside the calculate expression allow-list");

        // The rejection must be total: no row may carry a partial write.
        await AssertRowsAsync(SeedRows);
    }

    [IntegrationTest]
    public async Task Calculate_UnknownField_IsRejectedAndChangesNothing()
    {
        var response = await PostCalculateAsync(
            where: "1=1",
            calcExpression: """[{"field":"not_a_field","value":"x"}]""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertRowsAsync(SeedRows);
    }

    // -------------------------------------------------------------------------
    // Read-back oracle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads every feature back through the canonical FeatureServer query route and
    /// asserts exact attribute values and field-type fidelity. A wrong-but-well-formed
    /// implementation — one that updated every row, stringified the integer field, or
    /// truncated the widened label — fails here.
    /// </summary>
    private async Task AssertRowsAsync((long Id, string Label, int Score, string? Note)[] expected)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_routeLayerId}/query"
            + "?where=1%3D1&outFields=*&returnGeometry=false&orderByFields=id&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Field-type fidelity: calculate must not rewrite the schema it edits.
        var fields = root.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetString()!, StringComparer.OrdinalIgnoreCase);
        fields["score"].Should().Be("esriFieldTypeInteger");
        fields["label"].Should().Be("esriFieldTypeString");

        var features = root.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(expected.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            var attributes = features[i].GetProperty("attributes");
            var actualId = Convert.ToInt64(attributes.GetProperty("id").GetInt64(), CultureInfo.InvariantCulture);
            actualId.Should().Be(expected[i].Id);
            attributes.GetProperty("label").GetString().Should().Be(expected[i].Label, $"row {expected[i].Id} label");

            var score = attributes.GetProperty("score");
            score.ValueKind.Should().Be(JsonValueKind.Number, $"row {expected[i].Id} score must stay numeric");
            score.GetInt32().Should().Be(expected[i].Score, $"row {expected[i].Id} score");

            var note = attributes.GetProperty("note");
            if (expected[i].Note is null)
            {
                note.ValueKind.Should().Be(JsonValueKind.Null, $"row {expected[i].Id} note stays null");
            }
            else
            {
                note.GetString().Should().Be(expected[i].Note, $"row {expected[i].Id} note");
            }
        }
    }

    private Task<HttpResponseMessage> PostCalculateAsync(string where, string calcExpression)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["calcExpression"] = calcExpression
        });

        return _fixture.Client.PostAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_routeLayerId}/calculate", content);
    }

    /// <summary>
    /// Resolves the FeatureServer route id of the published layer from the service
    /// document rather than assuming an ordinal, so the proof cannot silently edit
    /// a different layer.
    /// </summary>
    private async Task<int> ResolveRouteLayerIdAsync()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var layer = document.RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Calculate proof layer");
        return layer.GetProperty("id").GetInt32();
    }
}
