// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DuckDB.NET.Data;
using Honua.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies the spatial extension is loaded onto every fresh DuckDB connection.
/// LOAD is connection-scoped in DuckDB; if the bootstrap only loaded on the first
/// connection, subsequent connections would fail when query execution references
/// spatial ST_* functions.
/// </summary>
public sealed class DuckDBSpatialBootstrapTests
{
    [Fact]
    public async Task EnsureSpatialExtensionAsync_LoadsSpatialOnEveryConnection()
    {
        var bootstrap = new DuckDBSpatialBootstrap(
            extensionPath: null,
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);

        // First connection — INSTALL + LOAD.
        await using (var first = new DuckDBConnection("Data Source=:memory:"))
        {
            await first.OpenAsync();
            await bootstrap.EnsureSpatialExtensionAsync(first, CancellationToken.None);
            await AssertSpatialFunctionAvailableAsync(first);
        }

        // Second connection — different in-memory database, no shared session state.
        // The previous connection's LOAD does not transfer; the bootstrap must LOAD
        // again or ST_Point will fail.
        await using (var second = new DuckDBConnection("Data Source=:memory:"))
        {
            await second.OpenAsync();
            await bootstrap.EnsureSpatialExtensionAsync(second, CancellationToken.None);
            await AssertSpatialFunctionAvailableAsync(second);
        }
    }

    private static async Task AssertSpatialFunctionAvailableAsync(DuckDBConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ST_AsText(ST_Point(1.0, 2.0))";
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        Assert.Contains("POINT", result!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureSpatialExtensionAsync_WithExternalSources_LoadsExtensionsAndCreatesViewsPerConnection()
    {
        var options = new DuckDBOptions
        {
            HttpFs = new DuckDBHttpFsOptions
            {
                S3 = new DuckDBS3Options
                {
                    Region = "us-east-1",
                    UrlStyle = "path"
                }
            },
            Layers =
            [
                new DuckDBLayerOptions
                {
                    Id = 3,
                    Table = "parcels",
                    ExternalSource = new DuckDBExternalSourceOptions
                    {
                        Path = "s3://county/parcels/*.parquet",
                        UnionByName = true
                    }
                }
            ]
        };

        using var bootstrap = new DuckDBSpatialBootstrap(
            extensionPath: "/opt/duckdb/extensions",
            additionalExtensions: DuckDBExternalSourceSql.GetRequiredExtensions(options),
            connectionSettingCommands: DuckDBExternalSourceSql.BuildConnectionSettingCommands(options),
            externalSourcePlans: DuckDBExternalSourceSql.BuildExternalSourcePlans(options.Layers),
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);

        var first = new RecordingDbConnection();
        await bootstrap.EnsureSpatialExtensionAsync(first, CancellationToken.None);

        Assert.Contains("SET extension_directory='/opt/duckdb/extensions'", first.Commands);
        Assert.Contains("INSTALL spatial", first.Commands);
        Assert.Contains("LOAD spatial", first.Commands);
        Assert.Contains("INSTALL httpfs", first.Commands);
        Assert.Contains("LOAD httpfs", first.Commands);
        Assert.Contains("SET s3_region = 'us-east-1'", first.Commands);
        Assert.Contains("SET s3_url_style = 'path'", first.Commands);
        Assert.Contains(
            "CREATE OR REPLACE TEMP VIEW \"parcels\" AS SELECT * FROM read_parquet('s3://county/parcels/*.parquet', union_by_name = true)",
            first.Commands);

        var second = new RecordingDbConnection();
        await bootstrap.EnsureSpatialExtensionAsync(second, CancellationToken.None);

        Assert.DoesNotContain(second.Commands, command => command.StartsWith("INSTALL ", StringComparison.Ordinal));
        Assert.Contains("LOAD spatial", second.Commands);
        Assert.Contains("LOAD httpfs", second.Commands);
        Assert.Contains(
            "CREATE OR REPLACE TEMP VIEW \"parcels\" AS SELECT * FROM read_parquet('s3://county/parcels/*.parquet', union_by_name = true)",
            second.Commands);
    }

    private sealed class RecordingDbConnection : DbConnection
    {
        public List<string> Commands { get; } = [];

        [AllowNull]
        public override string ConnectionString { get; set; } = "Data Source=:memory:";

        public override string Database => "recording";

        public override string DataSource => "recording";

        public override string ServerVersion => "1.5.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
            => new RecordingDbCommand(Commands);
    }

    private sealed class RecordingDbCommand(List<string> commands) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } = new RecordingDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            commands.Add(CommandText ?? string.Empty);
            return 0;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            commands.Add(CommandText ?? string.Empty);
            return Task.FromResult(0);
        }

        public override object? ExecuteScalar()
        {
            throw new NotSupportedException();
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter()
            => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new NotSupportedException();
    }

    private sealed class RecordingDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];

        public override int Count => _parameters.Count;

        public override object SyncRoot => this;

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear()
            => _parameters.Clear();

        public override bool Contains(object value)
            => _parameters.Contains((DbParameter)value);

        public override bool Contains(string value)
            => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index)
            => _parameters.ToArray().CopyTo(array, index);

        public override IEnumerator GetEnumerator()
            => _parameters.GetEnumerator();

        public override int IndexOf(object value)
            => _parameters.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName)
            => _parameters.FindIndex(parameter =>
                parameter.ParameterName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));

        public override void Insert(int index, object value)
            => _parameters.Insert(index, (DbParameter)value);

        public override void Remove(object value)
            => _parameters.Remove((DbParameter)value);

        public override void RemoveAt(int index)
            => _parameters.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        protected override DbParameter GetParameter(int index)
            => _parameters[index];

        protected override DbParameter GetParameter(string parameterName)
            => _parameters[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value)
            => _parameters[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
            {
                _parameters[index] = value;
                return;
            }

            _parameters.Add(value);
        }
    }
}
