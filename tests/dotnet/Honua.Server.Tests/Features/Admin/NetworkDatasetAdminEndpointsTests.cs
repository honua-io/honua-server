// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the network-dataset registry admin editing endpoints (#1882).
/// Covers register/list/get/update/delete happy paths, validation rejections (id slug,
/// edge/vertex identifier, srid, status), duplicate-id conflict, not-found handling, and
/// the anonymous-access auth gate.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class NetworkDatasetAdminEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "netds-admin-key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public NetworkDatasetAdminEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string NewId(string suffix) =>
        $"nd-{suffix}-{Guid.NewGuid():N}".Substring(0, 24).ToLowerInvariant();

    private static RegisterNetworkDatasetRequest BuildValidRequest(string id, string? name = null) => new()
    {
        Id = id,
        Name = name ?? $"Network {id}",
        EdgeTable = "public.ways",
        VertexTable = "public.ways_vertices_pgr",
        Srid = 4326,
        Status = "active",
        Description = "Integration test dataset",
    };

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_ValidPayload_Returns201WithDto()
    {
        var id = NewId("a");
        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkDatasetDto>(_jsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(id, dto!.Id);
        Assert.Equal("public.ways", dto.EdgeTable);
        Assert.Equal("public.ways_vertices_pgr", dto.VertexTable);
        Assert.Equal(4326, dto.Srid);
        Assert.Equal("active", dto.Status);
        Assert.Equal(1, dto.TopologyVersion);
        Assert.False(dto.NeedsRebuild);
        Assert.Equal("admin", dto.CreatedBy);

        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var generation = connection.CreateCommand();
        generation.CommandText = """
            SELECT generation, source_revision, state, row_version,
                   edge_table, vertex_table, srid, COUNT(*) OVER ()
            FROM honua.network_topology_generations
            WHERE dataset_id = @id
            """;
        generation.Parameters.AddWithValue("id", id);
        await using var reader = await generation.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal("active", reader.GetString(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal("public.ways", reader.GetString(4));
        Assert.Equal("public.ways_vertices_pgr", reader.GetString(5));
        Assert.Equal(4326, reader.GetInt32(6));
        Assert.Equal(1, reader.GetInt64(7));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_DuplicateId_Returns409()
    {
        var id = NewId("dup");
        var first = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_UnsafeEdgeTable_Returns400()
    {
        var id = NewId("unsafe");
        var request = BuildValidRequest(id);
        request.EdgeTable = "public.ways; DROP TABLE honua.network_datasets;--";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_BadIdSlug_Returns400()
    {
        var request = BuildValidRequest("Not A Slug");
        request.Id = "Not A Slug";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_BadStatus_Returns400()
    {
        var id = NewId("status");
        var request = BuildValidRequest(id);
        request.Status = "archived";

        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/network-datasets")]
    public async Task Register_BadSrid_Returns400()
    {
        var id = NewId("srid");
        var request = BuildValidRequest(id);
        request.Srid = 0;

        var response = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}")]
    public async Task Get_AfterRegister_ReturnsDataset()
    {
        var id = NewId("get");
        var register = await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkDatasetDto>(_jsonOptions);
        Assert.Equal(id, dto!.Id);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets/{id}")]
    public async Task Get_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/admin/network-datasets/{NewId("missing")}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets")]
    public async Task List_IncludesRegisteredDataset()
    {
        var id = NewId("list");
        await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        var response = await _client.GetAsync("/api/v1/admin/network-datasets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var datasets = await response.Content.ReadFromJsonAsync<NetworkDatasetDto[]>(_jsonOptions);
        Assert.NotNull(datasets);
        Assert.Contains(datasets!, d => d.Id == id);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/network-datasets/{id}")]
    public async Task Update_ChangesMutableFields()
    {
        var id = NewId("upd");
        await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        var update = new UpdateNetworkDatasetRequest
        {
            Name = "Renamed dataset",
            Status = "disabled",
            Description = "Updated note",
        };

        var response = await _client.PutAsJsonAsync($"/api/v1/admin/network-datasets/{id}", update, _jsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<NetworkDatasetDto>(_jsonOptions);
        Assert.Equal("Renamed dataset", dto!.Name);
        Assert.Equal("disabled", dto.Status);
        Assert.Equal("Updated note", dto.Description);
        // Unsupplied fields are preserved.
        Assert.Equal("public.ways", dto.EdgeTable);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/network-datasets/{id}")]
    public async Task Update_LegacyTopologyMapping_RecordsReplacementActiveGeneration()
    {
        var id = NewId("mapupd");
        await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        var update = new UpdateNetworkDatasetRequest
        {
            EdgeTable = "public.ways_v2",
            VertexTable = "public.ways_vertices_v2",
            Srid = 3857,
        };

        var response = await _client.PutAsJsonAsync($"/api/v1/admin/network-datasets/{id}", update, _jsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var generations = connection.CreateCommand();
        generations.CommandText = """
            SELECT generation, state, edge_table, vertex_table, srid, COUNT(*) OVER ()
            FROM honua.network_topology_generations
            WHERE dataset_id = @id
            ORDER BY generation
            """;
        generations.Parameters.AddWithValue("id", id);
        await using var reader = await generations.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("retired", reader.GetString(1));
        Assert.Equal(2, reader.GetInt64(5));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal("active", reader.GetString(1));
        Assert.Equal("public.ways_v2", reader.GetString(2));
        Assert.Equal("public.ways_vertices_v2", reader.GetString(3));
        Assert.Equal(3857, reader.GetInt32(4));
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/network-datasets/{id}")]
    public async Task Update_UnsafeVertexTable_Returns400()
    {
        var id = NewId("updbad");
        await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        var update = new UpdateNetworkDatasetRequest { VertexTable = "bad table name" };
        var response = await _client.PutAsJsonAsync($"/api/v1/admin/network-datasets/{id}", update, _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/network-datasets/{id}")]
    public async Task Update_Missing_Returns404()
    {
        var update = new UpdateNetworkDatasetRequest { Name = "x" };
        var response = await _client.PutAsJsonAsync($"/api/v1/admin/network-datasets/{NewId("updmiss")}", update, _jsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/network-datasets/{id}")]
    public async Task Delete_RemovesDataset()
    {
        var id = NewId("del");
        await _client.PostAsJsonAsync("/api/v1/admin/network-datasets", BuildValidRequest(id), _jsonOptions);

        var delete = await _client.DeleteAsync($"/api/v1/admin/network-datasets/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await _client.GetAsync($"/api/v1/admin/network-datasets/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/network-datasets/{id}")]
    public async Task Delete_Missing_Returns404()
    {
        var response = await _client.DeleteAsync($"/api/v1/admin/network-datasets/{NewId("delmiss")}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/network-datasets")]
    public async Task List_Anonymous_IsDenied()
    {
        using var anonymous = _fixture.CreateClient();
        var response = await anonymous.GetAsync("/api/v1/admin/network-datasets");
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403 for anonymous admin access but got {(int)response.StatusCode}");
    }
}
