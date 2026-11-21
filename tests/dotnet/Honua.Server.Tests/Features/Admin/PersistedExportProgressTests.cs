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
public sealed class PersistedExportProgressTests(PersistedExportProgressTests.ProgressHost host)
    : IClassFixture<PersistedExportProgressTests.ProgressHost>
{
    private Honua.Server.Features.Infrastructure.Progress.PostgresUniversalProgressStore Store => host.Store;

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Endpoint("GET /api/v1/admin/operations/active")]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task ListOperations_InvalidExportRecord_KeepsHealthyOperationsVisible(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var printId = NewId();
        await Store.SetProgressAsync(printId, PrintProgress.CreateInitial(printId, "pdf", "map", 1));
        var exportId = NewId();
        await Store.SetProgressAsync(exportId, ExportProgress.CreateInitial(exportId, "csv", "svc", 3, 1));
        var fixture = await host.GetApplicationAsync();
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
        var fixture = await host.GetApplicationAsync();
        var path = $"/api/v1/admin/operations/{invalidId}";
        var response = cancel
            ? await fixture.Client.PostAsync($"{path}/cancel", null)
            : await fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Persisted export progress is invalid. Submit a new export request.");
        body.Should().NotContain("JsonException").And.NotContain("System.Text.Json");
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetActiveOperationsAsync_InvalidExportRecord_ReturnsHealthyExport(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var healthyId = NewId();
        await Store.SetProgressAsync(healthyId, ExportProgress.CreateInitial(healthyId, "csv", "svc", 3, 1));

        var operations = await Store.GetActiveOperationsAsync<ExportProgress>(OperationType.Export);

        operations.Should().Contain(p => p.JobId == healthyId).And.NotContain(p => p.JobId == invalidId);
    }

    private static string NewId() => $"persisted-export-{Guid.NewGuid():N}";

    private async Task<string> SeedInvalidExportAsync(bool legacyShape)
    {
        var id = NewId();
        await Store.SetProgressAsync(id, ExportProgress.CreateInitial(id, "csv", "svc", 3, 1));
        var table = host.Harness.Table("operation_progress");
        var idParameter = new NpgsqlParameter("id", id);
        var json = JsonNode.Parse((await host.Harness.ScalarAsync<string>(
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
        await host.Harness.ExecuteAsync(
            $"UPDATE {table} SET payload = @payload WHERE operation_id = @id",
            new NpgsqlParameter("payload", json.ToJsonString()),
            new NpgsqlParameter("id", id));
        return id;
    }

    // One harness and one host for the class. Every case seeds records under its own ids and
    // asserts only on those ids, so records an earlier case left behind do not change what a case
    // proves. The host starts on first use, so a filtered run of the store-only cases skips it.
    public sealed class ProgressHost : IAsyncLifetime
    {
        private readonly DatabaseFixtureAdapter _database = new();
        private ControlPlaneStateHarness? _harness;
        private WebAppFixture? _application;

        internal ControlPlaneStateHarness Harness
            => _harness ?? throw new InvalidOperationException("The host must be initialized first.");

        internal Honua.Server.Features.Infrastructure.Progress.PostgresUniversalProgressStore Store { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await _database.InitializeAsync();
            _harness = await ControlPlaneStateHarness.CreateAsync(_database, nameof(PersistedExportProgressTests));
            Store = _harness.CreateProgressStore();
        }

        public async Task<WebAppFixture> GetApplicationAsync()
        {
            if (_application is null)
            {
                var application = new WebAppFixture().ReplaceRequestService<IUniversalProgressStore>(Store);
                try
                {
                    await application.InitializeAsync();
                }
                catch
                {
                    await application.DisposeAsync();
                    throw;
                }

                _application = application;
            }

            return _application;
        }

        public async Task DisposeAsync()
        {
            try
            {
                await (_application?.DisposeAsync() ?? Task.CompletedTask);
            }
            finally
            {
                await DisposeStorageAsync();
            }
        }

        private async Task DisposeStorageAsync()
        {
            try
            {
                await (_harness?.DisposeAsync() ?? ValueTask.CompletedTask);
            }
            finally
            {
                await _database.DisposeAsync();
            }
        }
    }
}
