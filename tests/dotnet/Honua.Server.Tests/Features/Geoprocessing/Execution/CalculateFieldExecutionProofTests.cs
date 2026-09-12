// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Xunit;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Execution-content proof for <c>data-management.calculate-field</c> (#3937).
///
/// The prior evidence asserted only <c>success</c> and the presence of an updated
/// count; it never read the changed values back, so an implementation that
/// reported a count and wrote nothing (or wrote the wrong rows) passed. These
/// cases add literal rows to the editable PostGIS-backed FeatureServer layer
/// through the shared edit pipeline, execute the owning FeatureServer
/// <c>calculate</c> path over them, then read every one of those rows back
/// through the canonical protocol query route and assert exact post-state.
/// The oracle is the committed fixture plus arithmetic done here, never a
/// snapshot of what calculate produced.
///
/// The target is an editable Honua feature layer rather than a table published
/// with <c>PublishLayerAsync</c>: a published table is a read-only snapshot (it
/// advertises Query/Extract, and a refresh re-materializes it from its source),
/// so forcing Update onto one proves nothing about calculate. Every calculate
/// here is scoped to the object ids this proof created, so rows the shared seed
/// owns can neither satisfy nor disturb an assertion.
/// </summary>
[Collection("Database")]
[Trait("Category", "CalculateFieldExecutionProof")]
public sealed class CalculateFieldExecutionProofTests : IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const int LayerId = WebAppFixture.TestLayerId;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    // Object ids the edit pipeline assigned to SeedRows, in SeedRows order.
    private long[] _objectIds = [];

    // The committed fixture. Every expectation below is derived from these
    // literals, not from a prior run's output.
    private static readonly (string Name, int Population, string? Notes)[] SeedRows =
    [
        ("alpha", 10, "keep"),
        ("beta", 20, "keep"),
        ("gamma", 30, null),
        ("delta", 40, "keep"),
    ];

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // calculate is a bulk field UPDATE, and the shared FeatureServer edit pipeline
        // rejects an edit kind the publication never advertised (#4073). Declare Create so
        // the fixture rows go in through that same pipeline, and Update for calculate
        // itself — not Delete.
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update"]);

        _objectIds = await AddSeedRowsAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Calculate_FilteredRows_WritesExactValuesAndLeavesExcludedRowsUntouched()
    {
        // population * 2 + 1 and UPPER(name) || '-C' over the rows with population >= 20.
        var response = await PostCalculateAsync(
            where: OwnedRows("population >= 20"),
            calcExpression: """
                [{"field":"population","sqlExpression":"population * 2 + 1"},
                 {"field":"name","sqlExpression":"UPPER(name) || '-C'"}]
                """);

        using var document = ParseSuccessfulCalculate(response);
        document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(
            3, "exactly the three rows with population >= 20 match the filter");

        // Independent expectation from the committed fixture: the selected rows
        // carry the computed values, the excluded row is identical to seed.
        await AssertRowsAsync(
        [
            ("alpha", 10, "keep"),
            ("BETA-C", 41, "keep"),
            ("GAMMA-C", 61, null),
            ("DELTA-C", 81, "keep"),
        ]);
    }

    [Fact]
    public async Task Calculate_ObjectIdSelection_UpdatesOnlyTheNamedFeature()
    {
        var response = await PostCalculateAsync(
            where: "objectid = " + _objectIds[2].ToString(CultureInfo.InvariantCulture),
            calcExpression: """[{"field":"notes","value":"recalculated"}]""");

        using var document = ParseSuccessfulCalculate(response);
        document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(
            1, "only the row named by the filter may be updated");

        await AssertRowsAsync(
        [
            ("alpha", 10, "keep"),
            ("beta", 20, "keep"),
            ("gamma", 30, "recalculated"),
            ("delta", 40, "keep"),
        ]);
    }

    [Fact]
    public async Task Calculate_UnsupportedExpression_IsRejectedAndChangesNothing()
    {
        // md5() is a real PostgreSQL function that the calculate expression allow-list
        // (UPPER/LOWER/TRIM/LENGTH/COALESCE/NVL/CONCAT and arithmetic) does not admit, and
        // it is not injection-shaped. A subquery would be rejected too, but by the shared
        // InputValidationMiddleware SQL-injection guard before the request ever reaches
        // calculate — so it would prove the middleware, not the allow-list this case pins.
        var response = await PostCalculateAsync(
            where: OwnedRows(),
            calcExpression: """[{"field":"population","sqlExpression":"md5(name)"}]""");

        AssertRejected(response, "Unsupported expression for field 'population'",
            "md5 is outside the calculate expression allow-list");

        // The rejection must be total: no row may carry a partial write.
        await AssertRowsAsync(SeedRows);
    }

    [Fact]
    public async Task Calculate_UnknownField_IsRejectedAndChangesNothing()
    {
        var response = await PostCalculateAsync(
            where: OwnedRows(),
            calcExpression: """[{"field":"not_a_field","value":"x"}]""");

        AssertRejected(response, "Field 'not_a_field' does not exist in layer",
            "calculate may only target a field the layer schema declares");
        await AssertRowsAsync(SeedRows);
    }

    [Fact]
    public async Task Oracle_CalculateThatIgnoresTheFilter_IsRejected()
    {
        // A plausible wrong-but-well-formed calculate: the real route, the real
        // expressions and a real 200 with a plausible updated count — but applied to
        // every proof row because the population filter was dropped. Produced here by
        // asking for exactly that, so the response and the resulting rows are genuinely
        // valid.
        var response = await PostCalculateAsync(
            where: OwnedRows(),
            calcExpression: """
                [{"field":"population","sqlExpression":"population * 2 + 1"},
                 {"field":"name","sqlExpression":"UPPER(name) || '-C'"}]
                """);

        using var document = ParseSuccessfulCalculate(response);
        document.RootElement.GetProperty("updatedFeatureCount").GetInt32().Should().Be(4);

        // The frozen post-state of the population >= 20 proof rejects it: row 1 was
        // supposed to be untouched.
        Func<Task> assert = () => AssertRowsAsync(
        [
            ("alpha", 10, "keep"),
            ("BETA-C", 41, "keep"),
            ("GAMMA-C", 61, null),
            ("DELTA-C", 81, "keep"),
        ]);

        (await assert.Should().ThrowAsync<XunitException>(
            "the read-back oracle must reject a calculate that ignored its filter"))
            .Which.Message.Should().Contain("row 1");
    }

    // -------------------------------------------------------------------------
    // Fixture rows
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds <see cref="SeedRows"/> through the FeatureServer <c>addFeatures</c> route and
    /// returns the object ids the edit pipeline assigned, in <see cref="SeedRows"/> order.
    /// </summary>
    private async Task<long[]> AddSeedRowsAsync()
    {
        var features = new JsonArray();
        foreach (var (name, population, notes) in SeedRows)
        {
            features.Add(new JsonObject
            {
                ["attributes"] = new JsonObject
                {
                    ["name"] = name,
                    ["population"] = population,
                    ["notes"] = notes,
                },
            });
        }

        using var content = new StringContent(
            new JsonObject { ["features"] = features }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/addFeatures", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("addResults", out var addResults)
            || addResults.GetArrayLength() != SeedRows.Length
            || addResults.EnumerateArray().Any(result => !result.GetProperty("success").GetBoolean()))
        {
            throw new XunitException(
                "the fixture rows must be added through the edit pipeline. Body was: " + body);
        }

        return addResults.EnumerateArray().Select(result => result.GetProperty("objectId").GetInt64()).ToArray();
    }

    /// <summary>
    /// A where clause that confines a calculate to the rows this proof created, optionally
    /// narrowed further by <paramref name="predicate"/>.
    /// </summary>
    private string OwnedRows(string? predicate = null)
    {
        var owned = $"objectid IN ({ObjectIdList()})";
        return predicate is null ? owned : $"{owned} AND {predicate}";
    }

    private string ObjectIdList()
        => string.Join(",", _objectIds.Select(objectId => objectId.ToString(CultureInfo.InvariantCulture)));

    // -------------------------------------------------------------------------
    // Read-back oracle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads every proof row back through the canonical FeatureServer query route and
    /// asserts exact attribute values and field-type fidelity. A wrong-but-well-formed
    /// implementation — one that updated every row, stringified the integer field, or
    /// wrote nothing — fails here. Rows are named by their 1-based position in
    /// <see cref="SeedRows"/>.
    /// </summary>
    private async Task AssertRowsAsync((string Name, int Population, string? Notes)[] expected)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query"
            + $"?objectIds={ObjectIdList()}&outFields=*&returnGeometry=false&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Field-type fidelity: calculate must not rewrite the schema it edits.
        var fields = root.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetString()!, StringComparer.OrdinalIgnoreCase);
        fields["population"].Should().Be("esriFieldTypeInteger");
        fields["name"].Should().Be("esriFieldTypeString");

        var objectIdField = root.GetProperty("objectIdFieldName").GetString()!;
        var rows = root.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes"))
            .ToDictionary(attributes => attributes.GetProperty(objectIdField).GetInt64());
        rows.Should().HaveCount(expected.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            var row = $"row {i + 1}";
            rows.Should().ContainKey(_objectIds[i], $"{row} must still be readable");
            var attributes = rows[_objectIds[i]];

            attributes.GetProperty("name").GetString().Should().Be(expected[i].Name, $"{row} name");

            var population = attributes.GetProperty("population");
            population.ValueKind.Should().Be(JsonValueKind.Number, $"{row} population must stay numeric");
            population.GetInt32().Should().Be(expected[i].Population, $"{row} population");

            // Esri JSON may carry an empty attribute as null or omit it.
            var notes = attributes.TryGetProperty("notes", out var notesElement) ? notesElement : default;
            if (expected[i].Notes is null)
            {
                notes.ValueKind.Should().BeOneOf(
                    [JsonValueKind.Undefined, JsonValueKind.Null], $"{row} notes stays null");
            }
            else
            {
                notes.ValueKind.Should().Be(JsonValueKind.String, $"{row} notes");
                notes.GetString().Should().Be(expected[i].Notes, $"{row} notes");
            }
        }
    }

    /// <summary>
    /// The transport status and raw body of one calculate call. The body is carried so
    /// every assertion below can report it: a rejection envelope and a success envelope
    /// are both HTTP 200, so without the body a wrong-shaped response surfaces only as a
    /// bare <see cref="KeyNotFoundException"/> on a missing property.
    /// </summary>
    private readonly record struct CalculateOutcome(HttpStatusCode Status, string Body);

    private async Task<CalculateOutcome> PostCalculateAsync(string where, string calcExpression)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["calcExpression"] = calcExpression
        });

        using var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/calculate", content);
        return new CalculateOutcome(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Asserts that calculate returned the success envelope and hands back the parsed
    /// document. The caller owns the returned <see cref="JsonDocument"/>.
    /// </summary>
    private static JsonDocument ParseSuccessfulCalculate(CalculateOutcome outcome)
    {
        outcome.Status.Should().Be(HttpStatusCode.OK);

        var document = JsonDocument.Parse(outcome.Body);
        if (!document.RootElement.TryGetProperty("success", out var success))
        {
            document.Dispose();
            throw new XunitException(
                "calculate must answer with the CalculateResponse envelope. Body was: " + outcome.Body);
        }

        if (!success.GetBoolean())
        {
            document.Dispose();
            throw new XunitException("calculate reported failure. Body was: " + outcome.Body);
        }

        return document;
    }

    /// <summary>
    /// Asserts a calculate rejection. PA-070/PA-117: every GeoServices response — errors
    /// included — is HTTP 200, and the rejection travels in the body as
    /// <c>{"error":{"code":400,...}}</c>. Asserting the body code AND the reason is
    /// strictly stronger than asserting a transport status this protocol never emits:
    /// it pins which guard fired, not merely that something failed.
    /// </summary>
    private static void AssertRejected(CalculateOutcome outcome, string expectedReason, string because)
    {
        outcome.Status.Should().Be(HttpStatusCode.OK,
            "GeoServices signals errors in the body, never the transport status (PA-070/PA-117)");

        using var document = JsonDocument.Parse(outcome.Body);
        if (document.RootElement.TryGetProperty("success", out _)
            || !document.RootElement.TryGetProperty("error", out var error))
        {
            throw new XunitException(
                "a rejected calculate must answer with the GeoServices error envelope. Body was: " + outcome.Body);
        }

        error.GetProperty("code").GetInt32().Should().Be(400, because);

        var details = error.TryGetProperty("details", out var detailsElement)
            && detailsElement.ValueKind == JsonValueKind.Array
                ? string.Join(" | ", detailsElement.EnumerateArray().Select(detail => detail.GetString()))
                : string.Empty;
        details.Should().Contain(expectedReason, because);
    }
}
