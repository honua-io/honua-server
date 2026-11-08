// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Progress;
using Honua.Io.Export;
using Honua.Migration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class PersistedExportProgressTests : IAsyncLifetime
{
    private readonly ServiceProvider _services;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _cache;
    private readonly UniversalProgressStore _store;
    private readonly List<string> _ids = [];

    public PersistedExportProgressTests(RedisFixture redis)
    {
        _services = new ServiceCollection().AddStackExchangeRedisCache(options =>
            options.Configuration = redis.ConnectionString).BuildServiceProvider();
        _redis = ConnectionMultiplexer.Connect(redis.ConnectionString);
        _cache = _services.GetRequiredService<IDistributedCache>();
        _store = new UniversalProgressStore(_cache, NullLogger<UniversalProgressStore>.Instance, _redis);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _ids)
        {
            await _store.DeleteProgressAsync(id);
            await _cache.RemoveAsync($"export:request:{id}");
        }
        _redis.Dispose();
        await _services.DisposeAsync();
    }

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
                var operations = json["operations"]!.AsArray();
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
        _store.IsUsingFallback.Should().BeFalse();
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
        _store.IsUsingFallback.Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetActiveOperationsAsync_InvalidExportRecord_ReturnsHealthyExport(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        var healthyId = NewId();
        await _store.SetProgressAsync(healthyId, ExportProgress.CreateInitial(healthyId, "csv", "svc", 3, 1));

        var operations = await _store.GetActiveOperationsAsync<ExportProgress>(OperationType.Export);

        operations.Should().Contain(p => p.JobId == healthyId).And.NotContain(p => p.JobId == invalidId);
        _store.IsUsingFallback.Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_InvalidProgressAndRequest_QuarantinesOnlyInvalidJob(bool legacyShape)
    {
        var invalidId = await SeedInvalidExportAsync(legacyShape);
        await _cache.SetStringAsync($"export:request:{invalidId}",
            $$"""{"job":{"jobId":"{{invalidId}}","serviceName":"svc","layerId":3,"format":"csv"}}""");
        var healthyId = NewId();
        var job = new ExportJob(healthyId, "svc", 741, "roads", "csv", new FeatureQuery(), [],
            4326, 1, ExportGeometryType.None, null) { ServiceLayerIndex = 3 };
        await _store.SetProgressAsync(healthyId, ExportProgress.CreateInitial(healthyId, "csv", "svc", 3, 1));
        await _cache.SetStringAsync($"export:request:{healthyId}", JsonSerializer.Serialize(
            new ExportJobService.PersistedExportJobRequest { Job = job },
            ExportJsonContext.Default.PersistedExportJobRequest));
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.Complete();
        var logger = Substitute.For<ILogger<ExportJobService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var sut = new ExportJobService(_store, _cache, channel,
            _services.GetRequiredService<IServiceScopeFactory>(), logger);

        for (var pass = 0; pass < 2; pass++)
        {
            var recovered = new List<string>();
            await foreach (var id in sut.ReadQueuedJobIdsAsync())
            {
                recovered.Add(id);
            }
            recovered.Should().Contain(healthyId).And.NotContain(invalidId);
            (await _cache.GetStringAsync($"export:request:{invalidId}")).Should().BeNull();
            (await _store.GetActiveOperationIdsAsync()).Should().NotContain(invalidId);
            (await _store.GetActiveOperationIdsAsync(OperationType.Export)).Should().NotContain(invalidId);
        }
        logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log" &&
            call.GetArguments()[2]?.ToString()?.Contains(invalidId, StringComparison.Ordinal) == true)
            .Should().Be(1);
        var read = () => _store.GetProgressAsync(invalidId);
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("Persisted export progress is invalid.*");
        (await _cache.GetStringAsync($"export:request:{healthyId}")).Should().NotBeNull();
        _store.IsUsingFallback.Should().BeFalse();
    }

    private string NewId()
    {
        var id = $"persisted-export-{Guid.NewGuid():N}";
        _ids.Add(id);
        return id;
    }

    private async Task<string> SeedInvalidExportAsync(bool legacyShape)
    {
        var id = NewId();
        await _store.SetProgressAsync(id, ExportProgress.CreateInitial(id, "csv", "svc", 3, 1));
        var json = JsonNode.Parse((await _cache.GetStringAsync($"universal:progress:{id}"))!)!;
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
        await _cache.SetStringAsync($"universal:progress:{id}", json.ToJsonString());
        return id;
    }
}
