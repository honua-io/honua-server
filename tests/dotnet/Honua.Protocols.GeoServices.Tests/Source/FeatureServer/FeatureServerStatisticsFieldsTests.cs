// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Statistics must describe their result columns even when no usable values exist (#5045).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerStatisticsFieldsTests(FeatureServerStatisticsFieldsFixture fixture)
    : IClassFixture<FeatureServerStatisticsFieldsFixture>
{
    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_Post_DescribesNativeAggregateAliases(bool serviceQuery)
    {
        const string statistics = """
            [{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"FREQUENCY"},
             {"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"DBMS_COUNT_objectid"},
             {"statisticType":"sum","onStatisticField":"objectid","outStatisticFieldName":"DBMS_SUM_objectid"}]
            """;
        using var result = await QueryAsync(serviceQuery, statistics, "objectid IN (1,2,3)");
        var payload = Payload(result, serviceQuery);
        AssertFields(payload, ("FREQUENCY", "esriFieldTypeInteger"),
            ("DBMS_COUNT_objectid", "esriFieldTypeInteger"), ("DBMS_SUM_objectid", "esriFieldTypeDouble"));
        var attributes = payload.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("FREQUENCY").GetInt64().Should().Be(3);
        attributes.GetProperty("DBMS_COUNT_objectid").GetInt64().Should().Be(3);
        attributes.GetProperty("DBMS_SUM_objectid").GetDouble().Should().Be(6);
        payload.TryGetProperty("objectIdFieldName", out _).Should().BeFalse();
        payload.GetProperty("features")[0].TryGetProperty("geometry", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_Grouped_DescribesGroupKeysIncludingEmptyResults(bool serviceQuery, bool empty)
    {
        const string statistics = """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"n"}]""";
        using var result = await QueryAsync(serviceQuery, statistics, empty ? "1=0" : "objectid IN (1,2,3)", "name");
        var payload = Payload(result, serviceQuery);
        AssertFields(payload, ("name", "esriFieldTypeString"), ("n", "esriFieldTypeInteger"));
        var groupField = payload.GetProperty("fields")[0];
        groupField.GetProperty("alias").GetString().Should().Be("Place name");
        groupField.GetProperty("length").GetInt32().Should().Be(80);
        if (empty)
        {
            payload.GetProperty("features").GetArrayLength().Should().Be(0);
        }
        else
        {
            payload.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_AllNull_UsesDeclaredTypesInsteadOfSamplingValues(bool serviceQuery)
    {
        const string statistics = """
            [{"statisticType":"sum","onStatisticField":"population","outStatisticFieldName":"total"},
             {"statisticType":"avg","onStatisticField":"population","outStatisticFieldName":"average"},
             {"statisticType":"stddev","onStatisticField":"population","outStatisticFieldName":"deviation"},
             {"statisticType":"var","onStatisticField":"population","outStatisticFieldName":"variance"},
             {"statisticType":"min","onStatisticField":"category","outStatisticFieldName":"first_category"},
             {"statisticType":"max","onStatisticField":"timestamp","outStatisticFieldName":"latest"},
             {"statisticType":"min","onStatisticField":"population","outStatisticFieldName":"minimum"}]
            """;
        using var result = await QueryAsync(serviceQuery, statistics, "name = 'statistics-null-input'");
        var payload = Payload(result, serviceQuery);
        AssertFields(payload, ("total", "esriFieldTypeDouble"), ("average", "esriFieldTypeDouble"),
            ("deviation", "esriFieldTypeDouble"), ("variance", "esriFieldTypeDouble"),
            ("first_category", "esriFieldTypeString"), ("latest", "esriFieldTypeDate"),
            ("minimum", "esriFieldTypeInteger"));
        payload.GetProperty("features").GetArrayLength().Should().Be(1);
        payload.GetProperty("features")[0].GetProperty("attributes").EnumerateObject()
            .Should().OnlyContain(attribute => attribute.Value.ValueKind == JsonValueKind.Null);
    }

    [IntegrationTheory]
    [InlineData(false, "description", false)]
    [InlineData(true, "description", false)]
    [InlineData(false, "description", true)]
    [InlineData(true, "description", true)]
    [InlineData(false, "notes", false)]
    [InlineData(true, "notes", false)]
    [InlineData(false, "notes", true)]
    [InlineData(true, "notes", true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_HiddenOrMaskedInput_DoesNotExposeResultSchema(bool serviceQuery, string field, bool group)
    {
        var statistics = JsonSerializer.Serialize(new[]
        {
            new { statisticType = "count", onStatisticField = group ? "objectid" : field, outStatisticFieldName = "restricted_count" }
        });
        using var form = Form(statistics, "1=1", group ? field : null, serviceQuery);
        using var response = await _fixture.Client.PostAsync(Path(serviceQuery), form);
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"fields\"");
        body.Should().NotContain("\"features\"");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_AliasMatchesGroupKey_DescribesReturnedAggregateOnce(bool serviceQuery)
    {
        const string statistics = """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"name"}]""";
        using var result = await QueryAsync(serviceQuery, statistics, "objectid IN (1,2,3)", "name");
        AssertFields(Payload(result, serviceQuery), ("name", "esriFieldTypeInteger"));
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Statistics_DateOutputs_MatchTheirDeclaredEsriTypes(bool serviceQuery, bool group)
    {
        const string statistics = """
            [{"statisticType":"max","onStatisticField":"timestamp","outStatisticFieldName":"latest"},
             {"statisticType":"min","onStatisticField":"objectid","outStatisticFieldName":"first_id"}]
            """;
        using var result = await QueryAsync(serviceQuery, statistics, "name = 'statistics-date-input'", group ? "timestamp" : null);
        var payload = Payload(result, serviceQuery);
        var expected = new List<(string Name, string Type)>();
        if (group)
        {
            expected.Add(("timestamp", "esriFieldTypeDate"));
        }
        expected.Add(("latest", "esriFieldTypeDate"));
        expected.Add(("first_id", "esriFieldTypeInteger"));
        AssertFields(payload, expected.ToArray());
        var attributes = payload.GetProperty("features")[0].GetProperty("attributes");
        var expectedTimestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeMilliseconds();
        attributes.GetProperty("latest").GetInt64().Should().Be(expectedTimestamp);
        if (group)
        {
            attributes.GetProperty("timestamp").GetInt64().Should().Be(expectedTimestamp);
        }
    }

    [IntegrationTest]
    public void Statistics_CountBeyondInt32_UsesBigIntegerWithoutTruncation()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer }]
        };
        const long count = (long)int.MaxValue + 1;
        var response = FeatureServerQueryHandler.BuildStatisticsResponse(resource,
            [new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "objectid", OutStatisticFieldName = "n" }],
            null, [new Dictionary<string, object?> { ["n"] = count }], false);
        response.Fields.Should().ContainSingle().Which.Type.Should().Be("esriFieldTypeBigInteger");
        response.Features.Should().ContainSingle().Which.Attributes["n"].Should().Be(count);
    }

    private async Task<JsonDocument> QueryAsync(bool serviceQuery, string statistics, string where, string? group = null)
    {
        using var form = Form(statistics, where, group, serviceQuery);
        using var response = await _fixture.Client.PostAsync(Path(serviceQuery), form);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static string Path(bool serviceQuery)
        => "/rest/services/test/FeatureServer/" + (serviceQuery ? "query" : "0/query");

    private static FormUrlEncodedContent Form(string statistics, string where, string? group, bool serviceQuery)
    {
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["outStatistics"] = statistics,
            ["returnGeometry"] = "false"
        };
        if (group != null)
        {
            parameters["groupByFieldsForStatistics"] = group;
        }
        if (serviceQuery)
        {
            parameters["layers"] = "0";
        }
        return new FormUrlEncodedContent(parameters);
    }

    private static JsonElement Payload(JsonDocument result, bool serviceQuery)
        => serviceQuery ? result.RootElement.GetProperty("layers")[0] : result.RootElement;

    private static void AssertFields(JsonElement payload, params (string Name, string Type)[] expected)
    {
        var fields = payload.GetProperty("fields").EnumerateArray().ToArray();
        fields.Select(field => (field.GetProperty("name").GetString(), field.GetProperty("type").GetString()))
            .Should().Equal(expected.Select(field => ((string?)field.Name, (string?)field.Type)));
        fields.Should().OnlyContain(field => !field.GetProperty("editable").GetBoolean());
    }

}

public sealed class FeatureServerStatisticsFieldsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IFieldMaskSource>(new StatisticsFieldMaskSource());

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "name",
            Alias = "Place name",
            Type = MetadataV2FieldType.String,
            Length = 80,
            Nullable = true
        });
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "description",
            Type = MetadataV2FieldType.String,
            Hidden = true,
            Nullable = true
        });
        await App.InsertFeatureAsync(0, "statistics-null-input");
        await using var connection = await App.Postgres.GetConnectionAsync(App.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (0, NULL, '{"name":"statistics-date-input","timestamp":"2024-01-02T03:04:05Z"}');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private sealed class StatisticsFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create("notes"));
    }
}
