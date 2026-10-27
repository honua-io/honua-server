// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Net;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Databricks.Features.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Databricks.Tests;

/// <summary>
/// Admin connection-test driver for Databricks: connection-string shape, stubbed
/// <c>SELECT 1</c> success, and failure when the token is rejected.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksConnectionDriverTests
{
    [Theory]
    [InlineData("dapi-plain")]
    [InlineData("pa;ss=word")]
    [InlineData("x;host=other.example;insecuremode=true")]
    public void BuildConnectionString_RoundTripsHostWarehouseAndToken(string token)
    {
        var driver = CreateDriver();

        var connectionString = driver.BuildConnectionString(new ConnectionTarget(
            "https://dbc-abc123.cloud.databricks.com",
            443,
            "wh-123",
            "token",
            token,
            SslMode.Require));

        var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
        Assert.Equal(
            ["host", "token", "warehouseid"],
            parsed.Keys.Cast<string>().Select(static key => key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("dbc-abc123.cloud.databricks.com", parsed["Host"]);
        Assert.Equal("wh-123", parsed["WarehouseId"]);
        Assert.Equal(token, parsed["Token"]);
    }

    [Fact]
    public void InspectConnectionString_RequiresHttpsWorkspaceHost()
    {
        var driver = CreateDriver();
        var connectionString = driver.BuildConnectionString(new ConnectionTarget(
            "dbc-abc123.cloud.databricks.com",
            443,
            "wh-123",
            "token",
            "dapi-secret",
            SslMode.Require));

        var security = driver.InspectConnectionString(connectionString);

        Assert.Equal(["dbc-abc123.cloud.databricks.com"], security.Hosts);
        Assert.True(security.RequiresTls);
    }

    [Fact]
    public async Task TestConnectionAsync_StubSelectOne_ReturnsHealthy()
    {
        const string json = """
        {
          "statement_id": "probe",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [ { "name": "1", "position": 0 } ] } },
          "result": { "data_array": [ ["1"] ] }
        }
        """;
        var handler = new StubHttpMessageHandler().EnqueueJson(json);
        var driver = CreateDriver(handler);
        var connectionString = driver.BuildConnectionString(new ConnectionTarget(
            "dbc-abc123.cloud.databricks.com",
            443,
            "wh-123",
            "token",
            "dapi-good",
            SslMode.Require));

        var status = await driver.TestConnectionAsync(connectionString);

        Assert.Equal(ConnectionHealthStatus.Healthy, status);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("dapi-good", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TestConnectionAsync_BadToken_ReturnsUnhealthy()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson("{}", HttpStatusCode.Unauthorized);
        var driver = CreateDriver(handler);
        var connectionString = driver.BuildConnectionString(new ConnectionTarget(
            "dbc-abc123.cloud.databricks.com",
            443,
            "wh-123",
            "token",
            "bad-token",
            SslMode.Require));

        var status = await driver.TestConnectionAsync(connectionString);

        Assert.Equal(ConnectionHealthStatus.Unhealthy, status);
        Assert.Equal("bad-token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Registry_ListsDatabricksNextToRedshiftAndSnowflake()
    {
        var registry = new ConnectionDriverRegistry(
        [
            new NamedDriver(DataProviderNames.Redshift),
            new NamedDriver(DataProviderNames.Snowflake),
            CreateDriver()
        ]);

        Assert.Equal(DataProviderNames.Redshift, registry.Find("redshift")!.Provider);
        Assert.Equal(DataProviderNames.Snowflake, registry.Find("snowflake")!.Provider);
        Assert.Equal(DataProviderNames.Databricks, registry.Find("databricks")!.Provider);
        Assert.Equal(DataProviderNames.Databricks, registry.Find("databrickssql")!.Provider);
        Assert.Equal(DataProviderNames.Databricks, registry.Find("dbsql")!.Provider);
    }

    private static DatabricksConnectionDriver CreateDriver(HttpMessageHandler? handler = null)
        => new(NullLogger<DatabricksConnectionDriver>.Instance, handler);

    private sealed class NamedDriver(string provider) : IConnectionDriver
    {
        public string Provider => provider;

        public string BuildConnectionString(ConnectionTarget target) => provider;

        public Task<ConnectionHealthStatus> TestConnectionAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ConnectionHealthStatus.Healthy);
    }
}
