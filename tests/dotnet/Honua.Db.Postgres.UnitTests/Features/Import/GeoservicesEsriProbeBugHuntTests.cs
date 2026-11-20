// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoservicesEsriProbeBugHuntTests
{
    [Fact]
    public void CurvePolyline_IsNotSilentlyConvertedToNullGeometry()
    {
        var geoJson = ConvertEsriGeometry("""
            {
              "curvePaths": [
                [
                  [0,0], {"c":[[10,0],[5,5],[0,10]]}
                ]
              ]
            }
            """);

        Assert.NotNull(geoJson);
    }

    [Fact]
    public void CurvePolyline_CircularArc_PreservesCurvatureAndEndpoints()
    {
        var geoJson = ConvertEsriGeometry("""
            {"curvePaths": [[[1,0], {"c": [[0,1], [0,0]]}]]}
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson);
        var path = document.RootElement.GetProperty("coordinates")[0];
        Assert.True(path.GetArrayLength() > 2);
        Assert.Equal(1, path[0][0].GetDouble());
        Assert.Equal(0, path[0][1].GetDouble());
        Assert.Equal(0, path[path.GetArrayLength() - 1][0].GetDouble());
        Assert.Equal(1, path[path.GetArrayLength() - 1][1].GetDouble());
        foreach (var vertex in path.EnumerateArray())
        {
            var x = vertex[0].GetDouble();
            var y = vertex[1].GetDouble();
            // Esri c[1] is an interior point: this circle has center (0.5, 0.5).
            Assert.Equal(0.5, ((x - 0.5) * (x - 0.5)) + ((y - 0.5) * (y - 0.5)), precision: 6);
        }
    }

    [Fact]
    public void CurvePolyline_CubicBezier_SamplesCurveAndPreservesEndpointOrdinates()
    {
        var geoJson = ConvertEsriGeometry("""
            {"hasZ": true, "hasM": true,
             "curvePaths": [[[0,0,30,40], {"b": [[10,0,31,41], [3,5], [7,5]]}]]}
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson);
        var path = document.RootElement.GetProperty("coordinates")[0];
        Assert.Equal(33, path.GetArrayLength());
        Assert.Equal(5, path[16][0].GetDouble(), precision: 6);
        Assert.Equal(3.75, path[16][1].GetDouble(), precision: 6);
        Assert.Equal(30, path[0][2].GetDouble());
        Assert.Equal(40, path[0][3].GetDouble());
        Assert.Equal(30.5, path[16][2].GetDouble());
        Assert.Equal(40.5, path[16][3].GetDouble());
        Assert.Equal(31, path[32][2].GetDouble());
        Assert.Equal(41, path[32][3].GetDouble());
    }

    [Fact]
    public void EmptyPolyline_IsRetainedAsAnEmptyGeometry()
    {
        var geoJson = ConvertEsriGeometry("{\"paths\":[]}");

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson!);
        Assert.Equal("MultiLineString", document.RootElement.GetProperty("type").GetString());
        Assert.Empty(document.RootElement.GetProperty("coordinates").EnumerateArray());
    }

    [Fact]
    public void EmptyPolyline_IsInsertedAsEmptyWktInsteadOfNull()
    {
        var wkt = ConvertWkt("{\"paths\":[]}", hasZ: false, hasM: false);

        Assert.Equal("MULTILINESTRING EMPTY", wkt);
    }

    [Fact]
    public void MOnlyPoint_DoesNotLoseTheSourceMeasure()
    {
        var geoJson = ConvertEsriGeometry("""
            { "hasZ": false, "hasM": true, "x": 10, "y": 20, "m": 7 }
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson!);
        Assert.Equal(3, document.RootElement.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(7, document.RootElement.GetProperty("coordinates")[2].GetDouble());
    }

    [Fact]
    public void MOnlyPoint_UsesThePostgisMDimension()
    {
        var wkt = ConvertWkt("""
            { "hasZ": false, "hasM": true, "x": 10, "y": 20, "m": 7 }
            """, hasZ: false, hasM: true);

        Assert.Equal("POINT M (10 20 7)", wkt);
    }

    [Fact]
    public void LayerDimensions_AreUsedWhenGeometryOmitsDimensionFlags()
    {
        var wkt = ConvertWkt("""
            { "paths": [[[10, 20, 30, 40], [11, 21, 31, 41]]] }
            """, hasZ: true, hasM: true);

        Assert.StartsWith("MULTILINESTRING ZM ", wkt, StringComparison.Ordinal);
        Assert.Contains("30 40", wkt, StringComparison.Ordinal);
        Assert.Contains("31 41", wkt, StringComparison.Ordinal);
    }

    [Fact]
    public void BlobAttribute_IsDecodedToByteaValueInsteadOfString()
    {
        using var document = JsonDocument.Parse("\"AQID\"");
        var value = InvokePrivate(
            "ConvertJsonValue",
            document.RootElement,
            "esriFieldTypeBlob");

        Assert.IsType<byte[]>(value);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])value!);
    }

    [Fact]
    public void RasterField_IsNotMappedToText()
    {
        var value = InvokePrivate("MapEsriTypeToPgType", "esriFieldTypeRaster", null);

        Assert.NotEqual("TEXT", value);
    }

    [Fact]
    public void ZGeometry_UsesADimensionalPostgisColumn()
    {
        using var geometry = JsonDocument.Parse("""
            {
              "hasZ": true,
              "x": 10,
              "y": 20,
              "z": 30
            }
            """);
        var geoJson = ConvertEsriGeometry(geometry.RootElement.GetRawText());
        Assert.NotNull(geoJson);

        var layerInfo = new GeoservicesLayerInfo
        {
            Id = 1,
            Name = "Z points",
            GeometryType = "esriGeometryPoint",
            HasZ = true,
            Fields = []
        };
        var sql = (string)InvokePrivate(
            "BuildCreateTableSql",
            "honua_data",
            "z_points",
            layerInfo,
            4326)!;

        Assert.Contains("geometry(POINTZ, 4326)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CurvePolygon_CurveRings_DensifiesAndClassifiesIntoMultiPolygon()
    {
        var geoJson = ConvertEsriGeometry("""
            {
              "curveRings": [
                [
                  [0, 0], [0, 10], {"c": [[10, 10], [5, 12]]}, [10, 0], [0, 0]
                ]
              ]
            }
            """);

        Assert.NotNull(geoJson);
        using var document = JsonDocument.Parse(geoJson!);
        Assert.Equal("MultiPolygon", document.RootElement.GetProperty("type").GetString());
        var ring = document.RootElement.GetProperty("coordinates")[0][0];
        Assert.True(ring.GetArrayLength() > 5);
    }

    [Fact]
    public void PolylineAndMultiPoint_MOnly_DoNotPromoteMeasureToGeoJsonZ()
    {
        var lineGeoJson = ConvertEsriGeometry("""
            { "hasZ": false, "hasM": true, "paths": [[[0, 0, 100], [1, 1, 200]]] }
            """);
        var mpGeoJson = ConvertEsriGeometry("""
            { "hasZ": false, "hasM": true, "points": [[0, 0, 100], [1, 1, 200]] }
            """);

        Assert.NotNull(lineGeoJson);
        Assert.NotNull(mpGeoJson);
        using var lineDoc = JsonDocument.Parse(lineGeoJson!);
        using var mpDoc = JsonDocument.Parse(mpGeoJson!);
        Assert.Equal(2, lineDoc.RootElement.GetProperty("coordinates")[0][0].GetArrayLength());
        Assert.Equal(2, mpDoc.RootElement.GetProperty("coordinates")[0].GetArrayLength());
    }

    [Fact]
    public void HasHigherDimensionCoordinates_Distinguishes2DAnd3DCurveGeometries()
    {
        using var curve2D = JsonDocument.Parse("""
            {"curvePaths": [[[0, 0], {"c": [[10, 0], [5, 5]]}]]}
            """);
        using var curve3D = JsonDocument.Parse("""
            {"hasZ": true, "curveRings": [[[0, 0, 10], [0, 10, 10], {"c": [[10, 10, 15], [5, 12, 12]]}, [0, 0, 10]]]}
            """);
        using var curveMOnly = JsonDocument.Parse("""
            {"hasZ": false, "hasM": true, "curvePaths": [[[0, 0, 100], {"c": [[10, 0, 200], [5, 5, 150]]}]]}
            """);

        Assert.False((bool)InvokePrivate("HasHigherDimensionCoordinates", curve2D.RootElement)!);
        Assert.True((bool)InvokePrivate("HasHigherDimensionCoordinates", curve3D.RootElement)!);
        Assert.False((bool)InvokePrivate("HasHigherDimensionCoordinates", curveMOnly.RootElement)!);
    }

    [Fact]
    public async Task PostgresGeometryTopologyValidator_RepairAsync_UsesStAsEwkbToPreserveSrid()
    {
        var executedCommands = new List<string>();
        var connectionProvider = new Moq.Mock<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>();
        connectionProvider
            .Setup(p => p.OpenConnectionAsync(Moq.It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<System.Data.Common.DbConnection>(new FakeDbConnection(executedCommands)));
        var validator = new Honua.Db.Postgres.Features.Geometry.PostgresGeometryTopologyValidator(connectionProvider.Object);

        _ = await validator.RepairAsync([1, 1, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Contains(executedCommands, sql => sql.Contains("ST_AsEWKB(repaired)", StringComparison.Ordinal));
        Assert.DoesNotContain(executedCommands, sql => sql.Contains("ST_AsBinary(repaired)", StringComparison.Ordinal));
    }

    private sealed class FakeDbConnection(List<string> executedCommands) : System.Data.Common.DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "16.0";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand() => new FakeDbCommand(executedCommands);
    }

    private sealed class FakeDbCommand(List<string> executedCommands) : System.Data.Common.DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override System.Data.CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
        protected override System.Data.Common.DbConnection? DbConnection { get; set; }
        protected override System.Data.Common.DbParameterCollection DbParameterCollection { get; } = new FakeDbParameterCollection();
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar()
        {
            executedCommands.Add(CommandText);
            return "Self-intersection";
        }
        public override void Prepare() { }
        protected override System.Data.Common.DbParameter CreateDbParameter() => new Npgsql.NpgsqlParameter();
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
        {
            executedCommands.Add(CommandText);
            using var table = new System.Data.DataTable();
            table.Columns.Add("wkb", typeof(byte[]));
            table.Columns.Add("repaired_type", typeof(string));
            table.Columns.Add("original_type", typeof(string));
            table.Columns.Add("is_empty", typeof(bool));
            table.Rows.Add(new byte[] { 1, 1, 0, 0, 0x20, 0xE6, 0x10, 0, 0 }, "ST_Polygon", "ST_Polygon", false);
            return table.CreateDataReader();
        }
    }

    private sealed class FakeDbParameterCollection : System.Data.Common.DbParameterCollection
    {
        private readonly List<object> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) _items.Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains(value);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) { }
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf(value);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) => _items.Insert(index, value);
        public override void Remove(object value) => _items.Remove(value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { }
        protected override System.Data.Common.DbParameter GetParameter(int index) => (System.Data.Common.DbParameter)_items[index];
        protected override System.Data.Common.DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
        protected override void SetParameter(int index, System.Data.Common.DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, System.Data.Common.DbParameter value) => throw new NotSupportedException();
    }

    private static string? ConvertEsriGeometry(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (string?)InvokePrivate("ConvertEsriGeometryToGeoJson", document.RootElement);
    }

    private static string? ConvertWkt(string json, bool hasZ, bool hasM)
    {
        using var document = JsonDocument.Parse(json);
        return (string?)InvokePrivate(
            "ConvertEsriGeometryToWkt",
            document.RootElement,
            hasZ,
            hasM);
    }

    private static object? InvokePrivate(string methodName, params object?[] arguments)
    {
        var method = typeof(GeoservicesImportService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, arguments);
    }
}
