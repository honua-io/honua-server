// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [Theory]
    [InlineData(false, false, "Point", "ST_Force2D", 0)]
    [InlineData(true, false, "PointZ", "ST_Force3DZ", 2)]
    [InlineData(false, true, "PointM", "ST_Force3DM", 1)]
    [InlineData(true, true, "PointZM", "ST_Force4D", 3)]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishRetainedTable_PreservesDimensionsDomainsSubtypesAndRules(
        bool hasZ, bool hasM, string geometryType, string forceFunction, int zmFlag)
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE public.{_tableName} ALTER COLUMN geom TYPE geometry({geometryType},4326) "
            + $"USING {forceFunction}(geom)", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var domainName = $"Recovery_{_tableName}";
        var request = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Recovered {_tableName}",
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
                ["population"] = new()
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
            AttributeRules = [new()
            {
                Name = "retained-rule", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "true", TriggeringEvents = ["insert", "update"],
                ErrorMessage = "Retained source constraint"
            }]
        };
        var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        var published = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions);
        _layerId = published!.Data!.LayerId;

        using var scope = _fixture.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        var resource = snapshot.Graph.Resources.Should().ContainSingle(candidate =>
            candidate.SchemaFields.Any(field => field.Domain?.Name == domainName)).Subject;
        resource.Display!.HasZ.Should().Be(hasZ);
        resource.Display.HasM.Should().Be(hasM);
        resource.Subtypes!.DefaultSubtypeCode!.Value.GetInt32().Should().Be(100);
        resource.AttributeRules.Should().ContainSingle().Which.Should().BeEquivalentTo(request.AttributeRules[0]);

        var metadataResponse = await _client.GetAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("hasZ").GetBoolean().Should().Be(hasZ);
        metadata.RootElement.GetProperty("hasM").GetBoolean().Should().Be(hasM);
        metadata.RootElement.GetProperty("subtypeField").GetString().Should().Be("population");
        var field = metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "population");
        field.GetProperty("domain").GetProperty("codedValues")[0].GetProperty("code").GetInt32().Should().Be(100);

        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"SELECT count(*), min(ST_Zmflag(geom)) FROM public.{_tableName}", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
            reader.GetInt16(1).Should().Be((short)zmFlag);
        }

        // Repeating recovery must not create another publication or duplicate source data.
        var repeat = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        repeat.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
