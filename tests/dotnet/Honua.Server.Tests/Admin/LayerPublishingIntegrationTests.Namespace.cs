// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_RequiresResolvedTenantBeforeConnectionLookupAndKeepsAdminAuthorization()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("MultiTenancy:DefaultTenantId", "");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
            });
        try
        {
            await fixture.InitializeAsync();
            using var admin = fixture.CreateAdminClient();
            using var response = await admin.PostAsync($"/api/v1/admin/connections/{Guid.NewGuid()}/layers",
                JsonContent.Create(NamespaceRequest("maps"), options: _jsonOptions));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("namespaced publication requires a resolved tenant");
            using var anonymous = fixture.CreateClient();
            using var denied = await anonymous.PostAsync($"/api/v1/admin/connections/{Guid.NewGuid()}/layers",
                JsonContent.Create(NamespaceRequest("maps"), options: _jsonOptions));
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_SameScopeAppendsWithoutRetaggingExistingLayer()
    {
        var first = await PublishLayerAsync(NamespaceRequest("maps"));
        _layerId = first.LayerId;
        await CreatePostGisTableAsync(_fixture.Postgres.ConnectionString, _nonCanonicalIdTableName);
        var request = new PublishLayerRequest
        {
            Schema = _schema, Table = _nonCanonicalIdTableName, LayerName = "Second namespace layer",
            ServiceName = _serviceName, Namespace = "maps", PrimaryKey = "id", GeometryColumn = "geom"
        };
        var second = await PublishLayerAsync(request);
        second.LayerId.Should().NotBe(first.LayerId);
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var service = graph.Services.Single(candidate => candidate.Metadata.Name == _serviceName);
        var publications = graph.Publications.Where(candidate => candidate.ServiceId == service.Metadata.Id).ToArray();
        publications.Should().HaveCount(4);
        publications.Should().OnlyContain(candidate => candidate.Metadata.Namespace == "maps" && candidate.Metadata.Tenant == "public");
        publications.Select(candidate => candidate.LayerIndex).Distinct().Should().BeEquivalentTo([first.LayerId, second.LayerId]);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/enabled")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers/extents/refresh")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers/{layerId}/features/refresh")]
    public async Task PublishNamespace_ScopesEntireGraphIgnoresWireTenantAndServesOwner()
    {
        var connectionMetadata = new MetadataV2ObjectMetadata
        {
            Id = _connectionId.ToString("D"), Name = "Preserved connection", Title = "Existing connection",
            Tenant = "public", Namespace = "shared-connections", Attribution = "Retained credit"
        };
        var initialGraph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        await SaveMetadataGraphAsync(initialGraph with
        {
            Revision = initialGraph.Revision + 1,
            Connections = [.. initialGraph.Connections, new MetadataV2Connection
            {
                Metadata = connectionMetadata, Type = MetadataV2ConnectionType.Database, Provider = "postgis"
            }]
        });
        var request = JsonSerializer.SerializeToNode(NamespaceRequest("Maps_1.0"), _jsonOptions)!.AsObject();
        var generated = JsonSerializer.Serialize(NamespaceRequest("Maps_1.0"), LayerPublishingJsonContext.Default.PublishLayerRequest);
        JsonSerializer.Deserialize(generated, LayerPublishingJsonContext.Default.PublishLayerRequest)!.Namespace.Should().Be("Maps_1.0");
        request["tenant"] = "attacker-tenant";
        request["tenantId"] = "attacker-tenant";
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(body, _jsonOptions)!.Data!.LayerId;

        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        graph.Connections.Single(candidate => candidate.Metadata.Id == connectionMetadata.Id).Metadata
            .Should().BeEquivalentTo(connectionMetadata);
        var service = graph.Services.Single(candidate => candidate.Metadata.Name == _serviceName);
        var publications = graph.Publications.Where(candidate => candidate.ServiceId == service.Metadata.Id).ToArray();
        publications.Select(candidate => candidate.PublicationType).Should().BeEquivalentTo(
            [MetadataV2PublicationType.EsriFeatureLayer, MetadataV2PublicationType.StacCollection]);
        var resource = graph.Resources.Single(candidate => candidate.Metadata.Id == publications[0].ResourceId);
        var binding = graph.StorageBindings.Single(candidate => candidate.Metadata.Id == publications[0].StorageBindingId);
        foreach (var metadata in publications.Select(candidate => candidate.Metadata)
                     .Concat([service.Metadata, resource.Metadata, binding.Metadata]))
        {
            metadata.Namespace.Should().Be("Maps_1.0");
            metadata.Tenant.Should().Be("public", "only the trusted request context supplies ownership");
        }

        using var query = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json&where=1%3D1&outFields=*");
        query.StatusCode.Should().Be(HttpStatusCode.OK, await query.Content.ReadAsStringAsync());

        await AssertNamespaceAdminOperationsAsync(HttpStatusCode.OK);

        // A persisted foreign tenant's chain must not be served to this public-tenant caller.
        graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        await SaveMetadataGraphAsync(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Select(candidate => candidate.Metadata.Id == service.Metadata.Id
                ? candidate with { Metadata = candidate.Metadata with { Tenant = "foreign" } } : candidate).ToArray()
        });
        using var foreign = await _client.GetAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var graphBeforeDenials = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var sqlBeforeDenials = await ReadNamespaceLayerStateAsync();
        await AssertNamespaceAdminOperationsAsync(HttpStatusCode.NotFound);
        (await ReadNamespaceLayerStateAsync()).Should().Be(sqlBeforeDenials);
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(graphBeforeDenials);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_OmittedOrNullPreservesUnscopedPublication(bool omit)
    {
        var request = JsonSerializer.SerializeToNode(NamespaceRequest(null), _jsonOptions)!.AsObject();
        if (omit)
        {
            request.Remove("namespace");
        }
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers", JsonContent.Create(request));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(body, _jsonOptions)!.Data!.LayerId;
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var service = graph.Services.Single(candidate => candidate.Metadata.Name == _serviceName);
        service.Metadata.Namespace.Should().BeNull();
        service.Metadata.Tenant.Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("maps/other")]
    [InlineData("maps*")]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_InvalidIdentifierRejectsBeforeLayerWrites(string publicationNamespace)
    {
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(NamespaceRequest(publicationNamespace), options: _jsonOptions));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await LayerMetadataExistsAsync(_tableName)).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("other", "public", "maps")]
    [InlineData("maps", "foreign", "maps")]
    [InlineData(null, null, "maps")]
    [InlineData("maps", "public", null)]
    [Operation(Operations.Create)]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_RejectsExistingServiceScopeMismatchWithoutWrites(
        string? existingNamespace, string? existingTenant, string? requestedNamespace)
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var existing = new MetadataV2Service
        {
            Metadata = new() { Id = _serviceName, Name = _serviceName, Namespace = existingNamespace, Tenant = existingTenant },
            ServiceType = MetadataV2ServiceType.EsriFeatureService
        };
        await SaveMetadataGraphAsync(graph with { Revision = graph.Revision + 1, Services = [.. graph.Services, existing] });
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(NamespaceRequest(requestedNamespace), options: _jsonOptions));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await LayerMetadataExistsAsync(_tableName)).Should().BeFalse();
        _fixture.GetCurrentV2GraphSnapshot().Graph.Services.Single(candidate => candidate.Metadata.Id == _serviceName)
            .Should().BeEquivalentTo(existing);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_SqlOnlyLegacyServiceCannotBeAdoptedAndRollsBackLayer()
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "INSERT INTO honua.services(service_name, srid) VALUES (@name, 4326)", connection))
        {
            command.Parameters.AddWithValue("name", _serviceName);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
                JsonContent.Create(NamespaceRequest("maps"), options: _jsonOptions));
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
            (await LayerMetadataExistsAsync(_tableName)).Should().BeFalse("the final persisted-graph scope check rolls back layer SQL");
            _fixture.GetCurrentV2GraphSnapshot().Graph.Services.Should().NotContain(candidate => candidate.Metadata.Name == _serviceName);
        }
        finally
        {
            await using var connection = await _fixture.Postgres.GetConnectionAsync();
            await using var command = new NpgsqlCommand("DELETE FROM honua.services WHERE service_name = @name", connection);
            command.Parameters.AddWithValue("name", _serviceName);
            await command.ExecuteNonQueryAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishNamespace_ForeignConnectionMetadataRejectsAndRollsBackLayer()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var foreignConnection = new MetadataV2Connection
        {
            Metadata = new() { Id = _connectionId.ToString("D"), Name = "Foreign", Tenant = "foreign", Namespace = "connections" },
            Type = MetadataV2ConnectionType.Database, Provider = "postgis"
        };
        await SaveMetadataGraphAsync(graph with { Revision = graph.Revision + 1, Connections = [.. graph.Connections, foreignConnection] });
        var before = _fixture.GetCurrentV2GraphSnapshot().Graph;
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(NamespaceRequest("maps"), options: _jsonOptions));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await LayerMetadataExistsAsync(_tableName)).Should().BeFalse();
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(before);
    }

    private PublishLayerRequest NamespaceRequest(string? publicationNamespace) => new()
    {
        Schema = _schema, Table = _tableName, LayerName = "Namespace fixture", ServiceName = _serviceName,
        Namespace = publicationNamespace, GeometryColumn = "geom", GeometryType = "Point", Srid = 4326,
        PrimaryKey = "id", Fields = _idNamePopulationFields
    };

    private async Task AssertNamespaceAdminOperationsAsync(HttpStatusCode expected)
    {
        var route = $"/api/v1/admin/connections/{_connectionId}/layers";
        var serviceQuery = $"?serviceName={_serviceName}";
        using var list = await _client.GetAsync(route + serviceQuery);
        list.StatusCode.Should().Be(expected, await list.Content.ReadAsStringAsync());
        using var toggle = await _client.PutAsync($"{route}/{_layerId}/enabled{serviceQuery}",
            JsonContent.Create(new LayerEnabledRequest { Enabled = false }, options: _jsonOptions));
        toggle.StatusCode.Should().Be(expected, await toggle.Content.ReadAsStringAsync());
        using var bulk = await _client.PutAsync($"{route}/enabled{serviceQuery}",
            JsonContent.Create(new LayerEnabledRequest { Enabled = true }, options: _jsonOptions));
        bulk.StatusCode.Should().Be(expected, await bulk.Content.ReadAsStringAsync());
        using var extent = await _client.PostAsync($"{route}/extents/refresh{serviceQuery}", null);
        extent.StatusCode.Should().Be(expected, await extent.Content.ReadAsStringAsync());
        using var snapshot = await _client.PostAsync($"{route}/{_layerId}/features/refresh", null);
        snapshot.StatusCode.Should().Be(expected, await snapshot.Content.ReadAsStringAsync());
    }

    private async Task<string> ReadNamespaceLayerStateAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT to_jsonb(l)::text || COALESCE(
                (SELECT jsonb_agg(to_jsonb(f))::text FROM features f WHERE f.layer_id = l.layer_id), '[]')
            FROM honua.layers l WHERE l.layer_id = @layerId
            """, connection);
        command.Parameters.AddWithValue("layerId", _layerId!.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
