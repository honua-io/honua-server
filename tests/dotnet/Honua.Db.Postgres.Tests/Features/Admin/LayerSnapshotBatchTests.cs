// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class LayerSnapshotBatchTests
{
    [Fact]
    public async Task CopySnapshotPages_ReportsEachPageAndStopsOnShortPage()
    {
        var pages = new Queue<PostgreSqlLayerPublishingService.SnapshotPage>(
        [
            new(2, 10u, "(0,1)"),
            new(2, 10u, "(0,2)"),
            new(1, 10u, "(0,3)"),
        ]);
        var reported = new List<(long Done, long Total)>();

        var copied = await PostgreSqlLayerPublishingService.CopySnapshotPagesAsync(
            batchSize: 2,
            (hasCursor, _, _, _) =>
            {
                Assert.Equal(reported.Count > 0, hasCursor);
                return Task.FromResult(pages.Dequeue());
            },
            (done, total, _) =>
            {
                reported.Add((done, total));
                return Task.CompletedTask;
            },
            rowsTotal: 5,
            CancellationToken.None);

        Assert.Equal(5, copied);
        Assert.Equal([(2L, 5L), (4L, 5L), (5L, 5L)], reported);
    }

    [Fact]
    public async Task CopySnapshotPages_FullPageWithoutCursorFails()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlLayerPublishingService.CopySnapshotPagesAsync(
                2,
                (_, _, _, _) => Task.FromResult(new PostgreSqlLayerPublishingService.SnapshotPage(2, null, null)),
                report: null,
                rowsTotal: 4,
                CancellationToken.None));

        Assert.Contains("cursor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageSql_IsABoundedKeysetInsert()
    {
        var sql = PostgreSqlLayerPublishingService.BuildSnapshotPageSql(
            "\"honua\".\"features\"", "\"public\".\"roads\"", "src.\"geom\"", "'{}'::jsonb");

        Assert.Contains("INSERT INTO", sql, StringComparison.Ordinal);
        Assert.Contains("src.tableoid", sql, StringComparison.Ordinal);
        Assert.Contains("src.ctid", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @batchSize", sql, StringComparison.Ordinal);
        Assert.Contains("MATERIALIZED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnboundedSql_StaysASingleInsertSelect()
    {
        var sql = PostgreSqlLayerPublishingService.BuildUnboundedSnapshotSql(
            "\"honua\".\"features\"", "\"public\".\"roads\"", "src.\"geom\"", "'{}'::jsonb");

        Assert.Contains("INSERT INTO", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ctid", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Options_RejectNegativeThresholdAndEmptyBatch()
    {
        var negative = new LayerPublishingOptions { AsyncPublishRowThreshold = -1 };
        var empty = new LayerPublishingOptions { MaterializationBatchSize = 0 };

        Assert.False(LayerPublishingOptions.IsValidAsyncPublishRowThreshold(negative.AsyncPublishRowThreshold));
        Assert.False(LayerPublishingOptions.IsValidMaterializationBatchSize(empty.MaterializationBatchSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => negative.GetValidatedAsyncPublishRowThreshold());
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetValidatedMaterializationBatchSize());
    }
}
