// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class GeoservicesImportSavepointTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertFeaturesAsync_MultipleBatches_KeepTransactionLocksBounded(bool includeInvalidRows)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TEMP TABLE savepoint_features (objectid BIGSERIAL PRIMARY KEY, name VARCHAR(8))";
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        var service = CreateService();
        var layer = new GeoservicesLayerInfo
        {
            Id = 0,
            Name = "Savepoint fixture",
            Fields =
            [
                new() { Name = "OBJECTID", Type = "esriFieldTypeOID" },
                new() { Name = "Name", Type = "esriFieldTypeString", Length = 8 }
            ]
        };

        for (var batch = 0; batch < 3; batch++)
        {
            var features = Enumerable.Range(batch * 128, 128).Select(index => new ArcGisFeature
            {
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["OBJECTID"] = JsonSerializer.SerializeToElement(index + 1),
                    ["Name"] = JsonSerializer.SerializeToElement(
                        includeInvalidRows && index % 32 == 0 ? "too long for the database column" : $"ok{index}")
                }
            }).ToArray();
            var result = await InsertAsync(service, connection, transaction, layer, features, CancellationToken.None);
            result.Inserted.Should().Be(includeInvalidRows ? 124 : 128);
            result.Failed.Should().Be(includeInvalidRows ? 4 : 0);
            result.ObjectIdMap.Should().HaveCount(result.Inserted);

            // Measure the retained PostgreSQL resource, not calls to a mocked transaction.
            // Hundreds of rows reproduce the leak without exhausting a shared test server.
            await using var locks = connection.CreateCommand();
            locks.Transaction = transaction;
            locks.CommandText = "SELECT count(*) FROM pg_locks WHERE pid = pg_backend_pid() AND locktype = 'transactionid'";
            Convert.ToInt64(await locks.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeLessThanOrEqualTo(2);
        }

        await transaction.CommitAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM savepoint_features";
        Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(includeInvalidRows ? 372 : 384);
    }

    [Fact]
    public async Task InsertFeaturesAsync_Cancelled_PropagatesCancellation()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () => InsertAsync(CreateService(), connection, transaction,
            new GeoservicesLayerInfo { Id = 0, Name = "Cancelled", Fields = [] },
            [new ArcGisFeature()], cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InsertFeaturesAsync_CancelledDuringInsert_DoesNotConvertCancellationToRowFailure()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TEMP TABLE savepoint_features (objectid BIGSERIAL PRIMARY KEY, name TEXT);
                CREATE FUNCTION pg_temp.delay_import() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_sleep(30);
                    RETURN NEW;
                END $$;
                CREATE TRIGGER delay_import BEFORE INSERT ON savepoint_features
                    FOR EACH ROW EXECUTE FUNCTION pg_temp.delay_import();
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var insertion = InsertAsync(CreateService(), connection, transaction,
            new GeoservicesLayerInfo
            {
                Id = 0,
                Name = "Cancellation fixture",
                Fields = [new() { Name = "Name", Type = "esriFieldTypeString" }]
            },
            [new ArcGisFeature { Attributes = new() { ["Name"] = JsonSerializer.SerializeToElement("cancel") } }],
            cancellation.Token);

        var observedInFlight = false;
        try
        {
            await using var observer = await fixture.DataSource.OpenConnectionAsync();
            await using var probe = observer.CreateCommand();
            probe.CommandTimeout = 5;
            probe.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid AND wait_event = 'PgSleep')";
            probe.Parameters.AddWithValue("pid", connection.ProcessID);
            for (var attempt = 0; attempt < 100 && !insertion.IsCompleted; attempt++)
            {
                if ((bool)(await probe.ExecuteScalarAsync())!)
                {
                    observedInFlight = true;
                    break;
                }
                await Task.Delay(25);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        observedInFlight.Should().BeTrue("cancellation must interrupt the insert, not only preparation");
        var action = async () => { await insertion; };
        await action.Should().ThrowAsync<OperationCanceledException>();
        await transaction.RollbackAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM savepoint_features";
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(0);
    }

    private static Task<GeoservicesImportService.InsertFeaturesResult> InsertAsync(
        GeoservicesImportService service, NpgsqlConnection connection, NpgsqlTransaction transaction,
        GeoservicesLayerInfo layer, ArcGisFeature[] features, CancellationToken cancellationToken)
    {
        var method = typeof(GeoservicesImportService).GetMethod("InsertFeaturesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<GeoservicesImportService.InsertFeaturesResult>)method.Invoke(service,
            [connection, transaction, "pg_temp", "savepoint_features", layer, features, 4326, cancellationToken])!;
    }

    private static GeoservicesImportService CreateService() => new(
        new ArcGisRestClient(new HttpClient(), NullLogger<ArcGisRestClient>.Instance),
        Mock.Of<IAdoNetDatabaseConnectionProvider>(),
        Mock.Of<ICrsRegistry>(),
        new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
        NullLogger<GeoservicesImportService>.Instance,
        new GeoservicesLayerPublicationService(NullLogger<GeoservicesLayerPublicationService>.Instance));
}
