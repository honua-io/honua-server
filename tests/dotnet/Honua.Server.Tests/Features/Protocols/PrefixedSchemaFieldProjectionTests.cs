// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols;

/// <summary>
/// Regression coverage for honua-server#3392 (client-certification case
/// <c>NB-GPD-SCH-01</c>): a declared schema field whose name is not a bare SQL
/// identifier — the STAC EO extension's <c>eo:cloud_cover</c> is the canonical
/// case — was advertised by <c>/queryables</c>, the CSV header and the
/// GeoServices <c>fields</c> array, was filterable through CQL2, and was returned
/// by WFS 2.0, yet was silently absent from every OGC API Features
/// <c>properties</c> object and every GeoServices <c>attributes</c> object, and
/// was emitted as an empty CSV cell.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class PrefixedSchemaFieldProjectionTests : IAsyncLifetime
{
    private const string CollectionId = "0";
    private const string PrefixedFieldName = "eo:cloud_cover";
    private const string EncodedPrefixedFieldName = "eo%3Acloud_cover";
    private const double PrefixedFieldValue = 42;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field
            {
                Name = PrefixedFieldName,
                Type = MetadataV2FieldType.Double,
                Nullable = true,
                Description = "Cloud cover percentage",
            });

        // Give every seeded row of the layer a value for the prefixed field so an
        // omission in the payload is unambiguously a projection defect and not
        // missing data.
        await _fixture.Postgres.ExecuteAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 UPDATE features
                 SET attributes = attributes || jsonb_build_object('{PrefixedFieldName}', {PrefixedFieldValue}::double precision)
                 WHERE layer_id = {WebAppFixture.TestLayerId};
                 """),
            _fixture.CurrentSchema);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithPrefixedSchemaField_IncludesFieldInDefaultProjection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{CollectionId}/items?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var properties = await ReadFirstFeaturePropertiesAsync(response);
        properties.TryGetProperty(PrefixedFieldName, out var value).Should().BeTrue(
            "the default all-fields projection must carry every declared, non-hidden schema field");
        value.GetDouble().Should().Be(PrefixedFieldValue);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithPrefixedSchemaFieldInProperties_ReturnsOnlyThatField()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{CollectionId}/items?limit=1&properties={EncodedPrefixedFieldName}");

        // Explicitly asserted: before the fix this token was rejected outright as
        // "Invalid properties field", and relaxing only the protocol-side guard would
        // have turned it into an unhandled provider ArgumentException (HTTP 500).
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var properties = await ReadFirstFeaturePropertiesAsync(response);
        properties.TryGetProperty(PrefixedFieldName, out var value).Should().BeTrue();
        value.GetDouble().Should().Be(PrefixedFieldValue);

        properties.EnumerateObject().Select(property => property.Name)
            .Should().OnlyContain(name => name == PrefixedFieldName);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_AsCsvWithPrefixedSchemaField_EmitsValueInsteadOfEmptyCell()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{CollectionId}/items?limit=1&f=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThan(1);

        var headers = SplitCsvLine(lines[0]);
        var columnIndex = Array.IndexOf(headers, PrefixedFieldName);
        columnIndex.Should().BeGreaterThanOrEqualTo(0, "the CSV header already advertised the column");

        var cells = SplitCsvLine(lines[1]);
        cells.Length.Should().BeGreaterThan(columnIndex);
        cells[columnIndex].Should().NotBeEmpty("the advertised column must carry its value, not an empty cell");
        double.Parse(cells[columnIndex], CultureInfo.CurrentCulture).Should().Be(PrefixedFieldValue);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPrefixedSchemaFieldInOutFields_IncludesFieldInAttributes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?where=1%3D1&outFields={EncodedPrefixedFieldName}&f=json&resultRecordCount=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = document.RootElement
            .GetProperty("features")[0]
            .GetProperty("attributes");

        attributes.TryGetProperty(PrefixedFieldName, out var value).Should().BeTrue(
            "an explicitly requested, declared field must be delivered");
        value.GetDouble().Should().Be(PrefixedFieldValue);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithPrefixedSchemaFieldInSortBy_ReturnsOrderedPage()
    {
        // sortby travels through the same protocol-side name guard as properties and
        // lands on the provider's ORDER BY builder, which had the same identifier-vs-
        // jsonb-key confusion. Admitting the token protocol-side without fixing the
        // builder converts a 400 into a 500.
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{CollectionId}/items?limit=1&sortby=-{EncodedPrefixedFieldName}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidPropertiesFieldName_Returns400NotServerError()
    {
        // Converting a 400 into a 500 is precisely the regression risk of relaxing the
        // name guards, so assert the status explicitly rather than "not 200".
        string[] invalidFieldNames =
        [
            "name%27%29%3B%20DROP%20TABLE%20features%3B%20--",
            "name%3B%20DELETE%20FROM%20features",
            "na%22me",
            "name%20status",
            "definitely_not_a_declared_field",
        ];

        foreach (var encodedFieldName in invalidFieldNames)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/features/collections/{CollectionId}/items?limit=1&properties={encodedFieldName}");

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "properties={0} must be a client error, never a server error",
                encodedFieldName);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidSortByFieldName_Returns400NotServerError()
    {
        string[] invalidFieldNames =
        [
            "name%27%29%3B%20DROP%20TABLE%20features%3B%20--",
            "na%22me",
            "definitely_not_a_declared_field",
        ];

        foreach (var encodedFieldName in invalidFieldNames)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/features/collections/{CollectionId}/items?limit=1&sortby={encodedFieldName}");

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "sortby={0} must be a client error, never a server error",
                encodedFieldName);
        }
    }

    private static async Task<JsonElement> ReadFirstFeaturePropertiesAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);
        return features[0].GetProperty("properties").Clone();
    }

    // Minimal RFC 4180 splitter: enough for the fixture's values, which never contain
    // an escaped quote inside a quoted field.
    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var character in line.TrimEnd('\r'))
        {
            switch (character)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ',' when !inQuotes:
                    cells.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        cells.Add(current.ToString());
        return [.. cells];
    }
}
