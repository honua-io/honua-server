// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ContractTesting)]
    public async Task SnapshotPages_ConcurrentSourceMutation_PreservesOneSnapshot(bool refresh)
    {
        await UseServerFeatureStoreConnectionAsync();
        await InsertPostGisFeatureAsync("Second feature", 200, 2, 2);
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<LayerPublishingOptions>>().Value;
        var originalBatchSize = options.MaterializationBatchSize;
        options.MaterializationBatchSize = 1;
        try
        {
            // Resolve through the real scoped registration after configuring one-row pages.
            var publisher = services.GetRequiredService<ILayerPublishingService>();
            var connectionString = await services.GetRequiredService<ISecureConnectionResolver>()
                .ResolveConnectionStringAsync(_connectionId);
            var request = LayerPublishRequestMapper.CreateDomain(new PublishLayerRequest
            {
                Schema = _schema,
                Table = _tableName,
                LayerName = "Consistent snapshot",
                ServiceName = _serviceName,
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNamePopulationFields,
                StorageMode = "source",
                Enabled = true,
            }, _connectionId, services.GetRequiredService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>().TenantId);
            if (refresh)
            {
                _layerId = (await publisher.PublishLayerAsync(connectionString, request)).LayerId;
            }

            var mutation = new ConcurrentSourceMutation(async () =>
            {
                // A separate connection commits after page one. Updating an already-read row
                // moves its physical tuple beyond the cursor; a later insert adds another row.
                await using var connection = await _fixture.Postgres.GetConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE public.{_tableName} SET name = 'Changed feature' WHERE id = 1";
                await command.ExecuteNonQueryAsync();
                await InsertPostGisFeatureAsync("Late feature", 300, 3, 3);
            });
            using (LayerSnapshotProgressScope.Begin(mutation))
            {
                if (refresh)
                {
                    Assert.NotNull(await publisher.RefreshMaterializedFeaturesAsync(connectionString, _layerId!.Value, _connectionId));
                }
                else
                {
                    _layerId = (await publisher.PublishLayerAsync(connectionString, request)).LayerId;
                }
            }

            Assert.True(mutation.Completed, "the concurrent source transaction must actually commit between pages");
            Assert.Equal(3, await GetSourceRowCountAsync());
            Assert.Equal(1, await GetSourceRowCountAsync("Changed feature"));
            Assert.Equal(2, await GetManagedStoreRowCountAsync(_layerId!.Value));
            await using var verify = await _fixture.Postgres.GetConnectionAsync();
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = $"""
                SELECT attributes->>'name'
                FROM "{_fixture.CurrentSchema}".features
                WHERE layer_id = @layerId
                ORDER BY attributes->>'name';
                """;
            verifyCommand.Parameters.AddWithValue("layerId", _layerId.Value);
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
            Assert.Equal(["Second feature", "Test Feature"], names);
        }
        finally
        {
            options.MaterializationBatchSize = originalBatchSize;
        }
    }

    private sealed class ConcurrentSourceMutation(Func<Task> mutate) : ILayerSnapshotProgress
    {
        private bool _attempted;
        internal bool Completed { get; private set; }

        public async Task ReportAsync(long rowsMaterialized, long rowsTotal, CancellationToken cancellationToken)
        {
            if (_attempted || rowsMaterialized != 1)
            {
                return;
            }

            _attempted = true;
            Assert.Equal(2, rowsTotal);
            await mutate();
            Completed = true;
        }
    }
}
