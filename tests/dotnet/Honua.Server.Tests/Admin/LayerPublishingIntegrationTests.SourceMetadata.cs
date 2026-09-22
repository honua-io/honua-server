// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Publishing a retained table through the admin API carries the captured source metadata
/// (geometry dimensions, domains, subtypes, attribute rules) that the import path would
/// carry, held to the same capture caps (honua-server#4854 REQ-006).
/// </summary>
public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false, false, "Point", "ST_Force2D", 0)]
    [InlineData(true, false, "PointZ", "ST_Force3DZ", 2)]
    [InlineData(false, true, "PointM", "ST_Force3DM", 1)]
    [InlineData(true, true, "PointZM", "ST_Force4D", 3)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task PublishRetainedTable_WithSourceMetadata_PreservesDimensionsDomainsSubtypesAndRules(
        bool hasZ, bool hasM, string geometryType, string forceFunction, int zmFlag)
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE public.{_tableName} ALTER COLUMN geom TYPE geometry({geometryType},4326) "
            + $"USING {forceFunction}(geom)", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var domainName = $"Retained_{_tableName}";
        var request = CreateSourceMetadataPublishRequest(hasZ, hasM, domainName);

        var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        var published = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions);
        _layerId = published!.Data!.LayerId;

        var metadataResponse = await _client.GetAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        var metadataPayload = await metadataResponse.Content.ReadAsStringAsync();
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK, metadataPayload);
        using var metadata = JsonDocument.Parse(metadataPayload);
        // GeoServices omits hasZ/hasM when false.
        (metadata.RootElement.TryGetProperty("hasZ", out var servedZ) && servedZ.GetBoolean()).Should().Be(hasZ);
        (metadata.RootElement.TryGetProperty("hasM", out var servedM) && servedM.GetBoolean()).Should().Be(hasM);
        metadata.RootElement.GetProperty("subtypeField").GetString().Should().Be("population");
        var population = metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "population");
        population.GetProperty("domain").GetProperty("codedValues")[0].GetProperty("code").GetInt32().Should().Be(100);

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resource = snapshot.Graph.Resources.Should().ContainSingle(candidate =>
            candidate.SchemaFields.Any(field => field.Domain != null && field.Domain.Name == domainName)).Subject;
        resource.Display!.HasZ.Should().Be(hasZ);
        resource.Display.HasM.Should().Be(hasM);
        resource.Subtypes!.SubtypeField.Should().Be("population");
        resource.Subtypes.DefaultSubtypeCode!.Value.GetInt32().Should().Be(100);
        resource.AttributeRules.Should().ContainSingle().Which.Should().BeEquivalentTo(request.AttributeRules![0]);

        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"SELECT count(*), min(ST_Zmflag(geom)) FROM public.{_tableName}", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
            reader.GetInt16(1).Should().Be((short)zmFlag);
        }

        // Publishing the same retained table again must not create a second layer.
        var repeat = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        repeat.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTheory]
    [InlineData("codedValues", "fieldDomains coded-value domains are limited to 100 coded values.")]
    [InlineData("subtypes", "subtypes are limited to 100 per layer.")]
    [InlineData("attributeRules", "attributeRules are limited to 200 per layer.")]
    [InlineData("nullFieldOverrides", "subtypes fieldOverrides must not be null.")]
    [InlineData("nullTriggeringEvents", "attributeRules triggeringEvents must not be null.")]
    [InlineData("duplicateDomainKeys", "fieldDomains keys must be unique ignoring case.")]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WithSourceMetadataOverImportCaps_Returns400AndDoesNotCreateLayer(
        string oversized, string expectedReason)
    {
        var baseline = CreateSourceMetadataPublishRequest(hasZ: false, hasM: false, $"Capped_{_tableName}");
        var request = oversized switch
        {
            "codedValues" => CopyWith(baseline, fieldDomains: new Dictionary<string, MetadataV2FieldDomain>
            {
                ["population"] = new()
                {
                    Name = $"Capped_{_tableName}",
                    Type = "codedValue",
                    CodedValues = Enumerable.Range(1, LayerPublishSourceMetadataBounds.MaxCodedValuesPerDomain + 1)
                        .Select(code => new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(code), Name = $"v{code}" })
                        .ToArray()
                }
            }),
            "subtypes" => CopyWith(baseline, subtypes: baseline.Subtypes! with
            {
                Subtypes = Enumerable.Range(1, LayerPublishSourceMetadataBounds.MaxSubtypes + 1)
                    .Select(code => new MetadataV2Subtype { Code = JsonSerializer.SerializeToElement(code), Name = $"s{code}" })
                    .ToArray()
            }),
            "attributeRules" => CopyWith(baseline, attributeRules: Enumerable.Range(1, LayerPublishSourceMetadataBounds.MaxAttributeRules + 1)
                .Select(index => baseline.AttributeRules![0] with { Name = $"rule-{index}" })
                .ToArray()),
            "nullFieldOverrides" => CopyWith(baseline, subtypes: baseline.Subtypes! with
            {
                Subtypes = [baseline.Subtypes.Subtypes[0] with { FieldOverrides = null! }]
            }),
            "nullTriggeringEvents" => CopyWith(baseline, attributeRules:
                [baseline.AttributeRules![0] with { TriggeringEvents = null! }]),
            "duplicateDomainKeys" => CopyWith(baseline, fieldDomains: new Dictionary<string, MetadataV2FieldDomain>
            {
                ["population"] = baseline.FieldDomains!["Population"],
                ["Population"] = baseline.FieldDomains["Population"]
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(oversized), oversized, null)
        };

        var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        payload.Should().Contain(expectedReason);

        var listResponse = await _client.GetAsync(
            $"/api/v1/admin/connections/{_connectionId}/layers?serviceName={_serviceName}");
        var listPayload = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK, listPayload);
        var layers = JsonSerializer.Deserialize<ApiResponse<List<PublishedLayerSummary>>>(listPayload, _jsonOptions);
        layers!.Data.Should().BeEmpty();

        // The shared publishing service applies the same bounds for every caller, including import.
        var publish = () => _fixture.GetService<ILayerPublishingService>().PublishLayerAsync(
            _fixture.Postgres.ConnectionString,
            new LayerPublishRequest
            {
                Schema = request.Schema,
                Table = request.Table,
                LayerName = request.LayerName,
                PrimaryKey = request.PrimaryKey,
                ServiceName = request.ServiceName,
                FieldDomains = request.FieldDomains!,
                Subtypes = request.Subtypes,
                AttributeRules = request.AttributeRules
            });
        (await publish.Should().ThrowAsync<LayerPublishingException>())
            .Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
    }

    private PublishLayerRequest CreateSourceMetadataPublishRequest(bool hasZ, bool hasM, string domainName)
        => new()
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Retained {_tableName}",
            GeometryColumn = "geom",
            GeometryType = "Point",
            HasZ = hasZ,
            HasM = hasM,
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            ServiceName = _serviceName,
            FieldDomains = new Dictionary<string, MetadataV2FieldDomain>
            {
                ["Population"] = new()
                {
                    Name = domainName,
                    Type = "codedValue",
                    CodedValues = [new() { Code = JsonSerializer.SerializeToElement(100), Name = "Standard" }]
                }
            },
            Subtypes = new()
            {
                SubtypeField = "population",
                DefaultSubtypeCode = JsonSerializer.SerializeToElement(100),
                Subtypes = [new() { Code = JsonSerializer.SerializeToElement(100), Name = "Standard" }]
            },
            AttributeRules =
            [
                new()
                {
                    Name = "retained-rule",
                    Type = MetadataV2AttributeRuleType.Constraint,
                    ScriptExpression = "true",
                    TriggeringEvents = ["insert", "update"],
                    ErrorMessage = "Retained source constraint"
                }
            ]
        };

    private static PublishLayerRequest CopyWith(
        PublishLayerRequest source,
        IReadOnlyDictionary<string, MetadataV2FieldDomain>? fieldDomains = null,
        MetadataV2Subtypes? subtypes = null,
        IReadOnlyList<MetadataV2AttributeRule>? attributeRules = null)
        => new()
        {
            Schema = source.Schema,
            Table = source.Table,
            LayerName = source.LayerName,
            GeometryColumn = source.GeometryColumn,
            GeometryType = source.GeometryType,
            HasZ = source.HasZ,
            HasM = source.HasM,
            Srid = source.Srid,
            PrimaryKey = source.PrimaryKey,
            Fields = source.Fields,
            ServiceName = source.ServiceName,
            FieldDomains = fieldDomains ?? source.FieldDomains,
            Subtypes = subtypes ?? source.Subtypes,
            AttributeRules = attributeRules ?? source.AttributeRules
        };
}
