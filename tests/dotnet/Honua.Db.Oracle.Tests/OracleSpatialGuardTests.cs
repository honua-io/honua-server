// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Oracle.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Oracle.Tests;

/// <summary>
/// Verifies that <see cref="OracleSpatialGuard"/> refuses to read non-SDO geometry columns or
/// ArcSDE-versioned tables (IP / clean-room enforcement point).
/// </summary>
public class OracleSpatialGuardTests
{
    private const int LayerId = 11;

    private static OracleLayerMapping BuildMapping(
        string column = "SHAPE",
        string table = "PARCELS",
        string? schema = "GIS")
    {
        var storage = new LayerStorageMapping(
            TableName: table,
            SchemaName: schema,
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: column,
            StorageSrid: 4326);
        return OracleLayerMapping.FromStorage(LayerId, storage);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_StandardSdoGeometryColumn_PassesThrough()
    {
        var probe = new FakeProbe("SDO_GEOMETRY", versioningColumns: Array.Empty<string>());
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        await guard.EnsureStandardSpatialAsync(BuildMapping(), dataConnection: null, CancellationToken.None);

        Assert.Equal(1, probe.GeometryTypeCalls);
        Assert.Equal(1, probe.VersioningCalls);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_BlobColumn_ThrowsNotSupported()
    {
        var probe = new FakeProbe("BLOB", versioningColumns: Array.Empty<string>());
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            guard.EnsureStandardSpatialAsync(BuildMapping(), dataConnection: null, CancellationToken.None));

        Assert.Contains("BLOB", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SDO_GEOMETRY", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ArcSDE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_StGeometryColumn_ThrowsNotSupported()
    {
        var probe = new FakeProbe("ST_GEOMETRY", versioningColumns: Array.Empty<string>());
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            guard.EnsureStandardSpatialAsync(BuildMapping(), dataConnection: null, CancellationToken.None));

        Assert.Contains("ST_GEOMETRY", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not SDO_GEOMETRY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_NullDataType_ThrowsNotSupported()
    {
        // ALL_TAB_COLUMNS returned no row (e.g., owner mismatch or missing privilege) — refuse to
        // serve rather than guess. Operator must fix the binding or grant.
        var probe = new FakeProbe(dataType: null, versioningColumns: Array.Empty<string>());
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            guard.EnsureStandardSpatialAsync(BuildMapping(), dataConnection: null, CancellationToken.None));
    }

    [Theory]
    [InlineData("GDB_FROM_DATE")]
    [InlineData("GDB_TO_DATE")]
    [InlineData("SDE_STATE_ID")]
    public async Task EnsureStandardSpatialAsync_VersioningColumnPresent_ThrowsNotSupported(string versioningColumn)
    {
        var probe = new FakeProbe("SDO_GEOMETRY", versioningColumns: [versioningColumn]);
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            guard.EnsureStandardSpatialAsync(BuildMapping(), dataConnection: null, CancellationToken.None));

        Assert.Contains(versioningColumn, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Versioned enterprise-geodatabase tables are not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_RepeatedCalls_CacheProbeResult()
    {
        var probe = new FakeProbe("SDO_GEOMETRY", versioningColumns: Array.Empty<string>());
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);
        var mapping = BuildMapping();

        await guard.EnsureStandardSpatialAsync(mapping, dataConnection: null, CancellationToken.None);
        await guard.EnsureStandardSpatialAsync(mapping, dataConnection: null, CancellationToken.None);
        await guard.EnsureStandardSpatialAsync(mapping, dataConnection: null, CancellationToken.None);

        Assert.Equal(1, probe.GeometryTypeCalls);
        Assert.Equal(1, probe.VersioningCalls);
    }

    [Fact]
    public async Task EnsureStandardSpatialAsync_FailedProbe_InvalidatesCache()
    {
        var probe = new FailingProbe();
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);
        var mapping = BuildMapping();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureStandardSpatialAsync(mapping, dataConnection: null, CancellationToken.None));

        // Subsequent call retries the probe (does not return the cached failure).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureStandardSpatialAsync(mapping, dataConnection: null, CancellationToken.None));

        Assert.Equal(2, probe.CallCount);
    }

    private sealed class FakeProbe(string? dataType, IReadOnlyList<string> versioningColumns) : IOracleSpatialMetadataProbe
    {
        public int GeometryTypeCalls { get; private set; }

        public int VersioningCalls { get; private set; }

        public Task<string?> GetGeometryColumnTypeAsync(
            OracleLayerMapping mapping,
            DataConnection? dataConnection,
            CancellationToken cancellationToken)
        {
            GeometryTypeCalls++;
            return Task.FromResult(dataType);
        }

        public Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(
            OracleLayerMapping mapping,
            DataConnection? dataConnection,
            CancellationToken cancellationToken)
        {
            VersioningCalls++;
            return Task.FromResult(versioningColumns);
        }
    }

    private sealed class FailingProbe : IOracleSpatialMetadataProbe
    {
        public int CallCount { get; private set; }

        public Task<string?> GetGeometryColumnTypeAsync(
            OracleLayerMapping mapping,
            DataConnection? dataConnection,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("probe failed");
        }

        public Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(
            OracleLayerMapping mapping,
            DataConnection? dataConnection,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("probe failed");
    }
}
