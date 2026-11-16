// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

// Invalid persisted export records in the PostgreSQL progress store (the only shared store)
// must not hide healthy operations or leak serializer details through the admin endpoints.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class PersistedExportProgressTests(DatabaseFixtureAdapter database)
    : IClassFixture<DatabaseFixtureAdapter>, IAsyncLifetime
{
    private ControlPlaneStateHarness _harness = null!;
    private Honua.Server.Features.Infrastructure.Progress.PostgresUniversalProgressStore _store = null!;

    public async Task InitializeAsync()
    {
        _harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PersistedExportProgressTests));
        _store = _harness.CreateProgressStore();
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Endpoint("GET /api/v1/admin/operations/active")]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task ListOperations_InvalidExportRecord_KeepsHealthyOperationsVisible(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var printId = NewId();
        await _store.SetProgressAsync(printId, PrintProgress.CreateInitial(printId, "pdf", "map", 1));
        var exportId = NewId();
        await _store.SetProgressAsync(exportId, ExportProgress.CreateInitial(exportId, "csv", "svc", 3, 1));
        var fixture = new WebAppFixture().ReplaceRequestService<IUniversalProgressStore>(_store);
        await fixture.InitializeAsync();
        try
        {
            foreach (var path in new[] { "active", "active?type=Export", "type/Export", "type/Print" })
            {
                var response = await fixture.Client.GetAsync($"/api/v1/admin/operations/{path}");
                response.StatusCode.Should().Be(HttpStatusCode.OK, path);
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                var operations = json["Operations"]!.AsArray();
                var body = operations.ToJsonString();
                body.Should().NotContain(invalidId);
                body.Should().Contain(path == "type/Print" ? printId : exportId);
                if (path == "active")
                {
                    body.Should().Contain(printId);
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task ReadOperation_InvalidExportRecord_ReturnsSafeRecordProblem(bool legacyShape, bool cancel)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var fixture = new WebAppFixture().ReplaceRequestService<IUniversalProgressStore>(_store);
        await fixture.InitializeAsync();
        try
        {
            var path = $"/api/v1/admin/operations/{invalidId}";
            var response = cancel
                ? await fixture.Client.PostAsync($"{path}/cancel", null)
                : await fixture.Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Persisted export progress is invalid. Submit a new export request.");
            body.Should().NotContain("JsonException").And.NotContain("System.Text.Json");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetActiveOperationsAsync_InvalidExportRecord_ReturnsHealthyExport(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var healthyId = NewId();
        await _store.SetProgressAsync(healthyId, ExportProgress.CreateInitial(healthyId, "csv", "svc", 3, 1));

        var operations = await _store.GetActiveOperationsAsync<ExportProgress>(OperationType.Export);

        operations.Should().Contain(p => p.JobId == healthyId).And.NotContain(p => p.JobId == invalidId);
    }

    private static string NewId() => $"persisted-export-{Guid.NewGuid():N}";

    private async Task<string> SeedInvalidExportAsync(bool legacyShape)
    {
        var id = NewId();
        await _store.SetProgressAsync(id, ExportProgress.CreateInitial(id, "csv", "svc", 3, 1));
        var table = _harness.Table("operation_progress");
        var idParameter = new NpgsqlParameter("id", id);
        var json = JsonNode.Parse((await _harness.ScalarAsync<string>(
            $"SELECT payload FROM {table} WHERE operation_id = @id", idParameter))!)!;
        var progress = json["Data"]!.AsObject();
        if (legacyShape)
        {
            progress.Remove("ServiceLayerIndex").Should().BeTrue();
            progress["LayerId"] = 3;
        }
        else
        {
            progress["ServiceLayerIndex"] = "invalid";
        }
        await _harness.ExecuteAsync(
            $"UPDATE {table} SET payload = @payload WHERE operation_id = @id",
            new NpgsqlParameter("payload", json.ToJsonString()),
            new NpgsqlParameter("id", id));
        return id;
    }
}
