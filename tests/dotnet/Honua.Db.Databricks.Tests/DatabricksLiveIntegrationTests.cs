// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Databricks.Features.FeatureStore;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Tests;

/// <summary>
/// Live, opt-in integration tests against a real Databricks SQL Warehouse. These are
/// gated behind <c>HONUA_TEST_DATABRICKS=1</c> plus host/warehouse/token/table env vars
/// and report an explicit credential skip in normal CI where no workspace is available.
/// </summary>
[Trait("Category", "Databricks")]
public sealed class DatabricksLiveIntegrationTests
{
    private const string EnableVar = "HONUA_TEST_DATABRICKS";
    private const string HostVar = "HONUA_TEST_DATABRICKS_HOST";
    private const string WarehouseVar = "HONUA_TEST_DATABRICKS_WAREHOUSE";
    private const string TokenVar = "HONUA_TEST_DATABRICKS_TOKEN";
    private const string TableVar = "HONUA_TEST_DATABRICKS_TABLE";

    [RequiredEnvironmentVariablesFact(EnableVar, HostVar, WarehouseVar, TokenVar, TableVar)]
    public async Task QueryAsync_LiveWarehouse_ReturnsFeatures()
    {
        var (options, layer) = GetLiveConfiguration();

        using var httpClient = new HttpClient { BaseAddress = new Uri(options.Host) };
        var statementClient = new DatabricksStatementClient(httpClient, Options.Create(options));
        var registry = new DatabricksLayerMappingRegistry([layer]);
        var store = new DatabricksFeatureStore(
            registry,
            new DatabricksFeatureQueryBuilder(),
            new DatabricksFeatureDataAccess(statementClient));

        var result = await store.QueryAsync(layer.LayerId, new FeatureQuery { Limit = 5 }, CancellationToken.None);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, feature => Assert.NotNull(feature.Geometry));
    }

    private static (DatabricksOptions Options, DatabricksLayerMapping Layer) GetLiveConfiguration()
    {
        var host = Environment.GetEnvironmentVariable(HostVar)!;
        var warehouse = Environment.GetEnvironmentVariable(WarehouseVar)!;
        var token = Environment.GetEnvironmentVariable(TokenVar)!;
        var table = Environment.GetEnvironmentVariable(TableVar)!;

        var options = new DatabricksOptions
        {
            Host = host,
            WarehouseId = warehouse,
            Token = token,
            Catalog = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_CATALOG"),
            Schema = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_SCHEMA"),
        };

        var layer = new DatabricksLayerMapping
        {
            LayerId = 1,
            Table = table,
            Catalog = options.Catalog,
            Schema = options.Schema,
            GeometryColumn = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_GEOM") ?? "geom",
            PrimaryKeyColumn = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_PK") ?? "id",
            Srid = int.TryParse(Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_SRID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) ? srid : 4326,
            GeometryType = GeometryType.Point,
            AttributeColumns = [],
        };

        return (options, layer);
    }
}
