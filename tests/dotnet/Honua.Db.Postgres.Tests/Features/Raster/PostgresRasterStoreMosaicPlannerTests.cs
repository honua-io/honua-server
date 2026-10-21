// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>Checks scoped mosaic query construction and ownership of the settings result reader.</summary>
public sealed class PostgresRasterStoreMosaicPlannerTests
{
    [Fact]
    public async Task ScopedNearestMosaic_ClonesTypedParametersAndClosesFailedOrCancelledReaders()
    {
        // A closed connection suffices to inspect the protocol batch; this unit opens no database.
        await using var connection = new NpgsqlConnection();
        await using var command = new NpgsqlCommand("SELECT @marker, @payload, @ids", connection);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("marker", NpgsqlDbType.Integer, 17);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, new byte[] { 0, 127, 255 });
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, new long[] { 3, 1 });
        await using var batch = PostgresRasterStore.CreateScopedNearestMosaicBatch(command);
        batch.EnableErrorBarriers.Should().BeFalse("SET LOCAL and SELECT must share one implicit transaction");
        batch.Timeout.Should().Be(command.CommandTimeout);
        batch.BatchCommands.Should().HaveCount(2);
        batch.BatchCommands[0].CommandText.Should().Be("SELECT pg_catalog.set_config('jit', 'off', true)");
        batch.BatchCommands[1].CommandText.Should().Be("/* honua:framed-nearest-mosaic-jit-off */\n" + command.CommandText);
        command.Parameters.Should().HaveCount(3, "cloning must not remove the original parameters");
        for (var i = 0; i < command.Parameters.Count; i++)
        {
            var cloned = batch.BatchCommands[1].Parameters[i];
            cloned.Should().NotBeSameAs(command.Parameters[i]);
            cloned.NpgsqlDbType.Should().Be(command.Parameters[i].NpgsqlDbType);
            cloned.ParameterName.Should().Be(command.Parameters[i].ParameterName);
            cloned.Value.Should().BeEquivalentTo(command.Parameters[i].Value);
        }

        var settings = new DataTable();
        settings.Columns.Add("set_config", typeof(string));
        settings.Rows.Add("off");
        var raster = new DataTable();
        raster.Columns.Add("measured_raster", typeof(int));
        raster.Rows.Add(17);
        var success = new DataTableReader(new[] { settings, raster });
        var selected = await PostgresRasterStore.ReadScopedNearestMosaicResultAsync(success, default);
        selected.Should().BeSameAs(success);
        selected.IsClosed.Should().BeFalse("the caller owns the successful raster reader");
        (await selected.ReadAsync()).Should().BeTrue();
        selected.GetInt32(0).Should().Be(17, "the setting result must never be consumed as raster data");
        await selected.DisposeAsync();
        success.IsClosed.Should().BeTrue();

        var missing = new DataTableReader(settings);
        Func<Task> missingResult = async () =>
        {
            await PostgresRasterStore.ReadScopedNearestMosaicResultAsync(missing, default);
        };
        await missingResult.Should().ThrowAsync<InvalidOperationException>();
        missing.IsClosed.Should().BeTrue("a missing raster result must dispose the implicit-transaction reader");

        var cancelled = new DataTableReader(new[] { settings, raster });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Func<Task> cancelledResult = async () =>
        {
            await PostgresRasterStore.ReadScopedNearestMosaicResultAsync(cancelled, stop.Token);
        };
        await cancelledResult.Should().ThrowAsync<OperationCanceledException>();
        cancelled.IsClosed.Should().BeTrue("cancellation while advancing must dispose the implicit-transaction reader");
    }
}
