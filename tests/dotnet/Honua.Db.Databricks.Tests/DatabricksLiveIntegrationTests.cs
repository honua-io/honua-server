// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.FeatureStore;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Databricks.Tests;

/// <summary>
/// Live, opt-in integration tests against a real Databricks SQL Warehouse. These are
/// gated behind <c>HONUA_TEST_DATABRICKS=1</c> plus host/warehouse/token/table env vars
/// and are skipped (treated as a no-op pass) in normal CI where no workspace is available.
/// </summary>
[Trait("Category", "Databricks")]
public sealed class DatabricksLiveIntegrationTests
{
    private const string EnableVar = "HONUA_TEST_DATABRICKS";

    [Fact]
    public async Task QueryAsync_LiveWarehouse_ReturnsFeatures()
    {
        if (!IsEnabled(out var options, out var layer))
        {
            // No live workspace configured; nothing to assert in standard CI.
            return;
        }

        using var httpClient = new HttpClient { BaseAddress = new Uri(options.Host) };
        var statementClient = new DatabricksStatementClient(httpClient, Options.Create(options));
        var registry = new DatabricksLayerMappingRegistry([layer]);
        var store = new DatabricksFeatureStore(
            registry,
            new DatabricksFeatureQueryBuilder(),
            new DatabricksFeatureDataAccess(statementClient));

        var result = await store.QueryAsync(layer.LayerId, new FeatureQuery { Limit = 5 }, CancellationToken.None);

        Assert.True(result.TotalCount >= 0);
    }

    private static bool IsEnabled(out DatabricksOptions options, out DatabricksLayerMapping layer)
    {
        options = new DatabricksOptions();
        layer = default!;

        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVar), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var host = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_HOST");
        var warehouse = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_WAREHOUSE");
        var token = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_TOKEN");
        var table = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_TABLE");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(warehouse)
            || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(table))
        {
            return false;
        }

        options = new DatabricksOptions
        {
            Host = host,
            WarehouseId = warehouse,
            Token = token,
            Catalog = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_CATALOG"),
            Schema = Environment.GetEnvironmentVariable("HONUA_TEST_DATABRICKS_SCHEMA"),
        };

        layer = new DatabricksLayerMapping
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

        return true;
    }
}
