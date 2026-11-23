// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Warehouse;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;
using Honua.Core.Features.Warehouse.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Core.Tests.Features.Warehouse;

[Trait("Tier", "Fast")]
public sealed class WarehouseSourceCapabilityProviderTests
{
    [Fact]
    public async Task ListAllAsync_ReturnsOnlySupportedProvidersWithoutBigQueryOrCarto()
    {
        using var services = new ServiceCollection()
            .AddWarehouseAnalyticsServices()
            .BuildServiceProvider();
        var provider = services.GetRequiredService<IWarehouseSourceCapabilityProvider>();

        var all = await provider.ListAllAsync();

        Assert.Equal(4, all.Count);
        Assert.Contains(all, c => c.ProviderName == DataProviderNames.Redshift);
        Assert.Contains(all, c => c.ProviderName == DataProviderNames.Snowflake);
        Assert.Contains(all, c => c.ProviderName == DataProviderNames.Databricks);
        Assert.Contains(all, c => c.ProviderName == DataProviderNames.DuckDb);
        Assert.DoesNotContain(all, c => c.ProviderName.Contains("bigquery", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, c => c.ProviderName.Contains("carto", StringComparison.OrdinalIgnoreCase));
        Assert.All(all, c => Assert.True(c.ReadOnly));
    }

    [Theory]
    [InlineData("bigquery")]
    [InlineData("carto")]
    [InlineData("carto-sql")]
    public async Task GetCapabilitiesAsync_ExcludedProviders_ReportsUnsupported(string providerName)
    {
        var provider = new WarehouseSourceCapabilityProvider();

        var result = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = providerName,
            SourceId = "src-1",
        });

        Assert.Equal(WarehouseSourceAvailability.Unsupported, result.Availability);
        Assert.True(result.ReadOnly);
        Assert.Empty(result.SupportedSpatialPredicates);
        Assert.False(result.Aggregation.SupportsH3Aggregation);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_PerProviderMatrix_ReportsAccurateSpatialCrsAndH3Support()
    {
        var provider = new WarehouseSourceCapabilityProvider();

        var duckdb = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.DuckDb,
            SourceId = "s3-parcels",
            ExternalSourceUri = "s3://county-data/parcels.parquet",
            HasBboxCoveringColumn = true,
        });
        var snowflake = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.Snowflake,
            SourceId = "sf-parcels",
        });
        var databricks = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.Databricks,
            SourceId = "dbx-parcels",
        });
        var redshift = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.Redshift,
            SourceId = "rs-parcels",
        });

        Assert.True(duckdb.CrsBehavior.SupportsCrossSridFilter);
        Assert.True(duckdb.CrsBehavior.SupportsOutputReprojection);
        Assert.True(duckdb.Aggregation.SupportsH3Aggregation);
        Assert.Contains("spatial", duckdb.RequiredExtensions);
        Assert.Contains("httpfs", duckdb.RequiredExtensions);
        Assert.Contains("h3", duckdb.Aggregation.RequiredExtensions);
        Assert.Contains(SpatialRelationship.WithinDistance, duckdb.SupportedSpatialPredicates);

        Assert.False(snowflake.CrsBehavior.SupportsCrossSridFilter);
        Assert.True(snowflake.Aggregation.SupportsH3Aggregation);
        Assert.True(snowflake.Aggregation.SupportsHaving);

        Assert.False(databricks.CrsBehavior.SupportsCrossSridFilter);
        Assert.True(databricks.Aggregation.SupportsH3Aggregation);
        Assert.True(databricks.SupportsSqlFilterPushdown);

        Assert.False(redshift.CrsBehavior.SupportsCrossSridFilter);
        Assert.False(redshift.Aggregation.SupportsH3Aggregation);
        Assert.True(redshift.Aggregation.SupportsStatistics);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithRuntimeAvailabilityAndDirtyInputs_RedactsSecrets()
    {
        var runtimeState = new WarehouseRuntimeAvailabilityState(
        [
            new KeyValuePair<string, (bool Enabled, string? Reason)>(
                DataProviderNames.Snowflake,
                (false, "Token=super-secret-token disabled by policy")),
        ]);
        var provider = new WarehouseSourceCapabilityProvider(runtimeState: runtimeState);

        var disabled = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.Snowflake,
            SourceId = "s3://user:topsecret@bucket/data.parquet?access_key=AKIA123456&secret=mysecret",
        });

        Assert.Equal(WarehouseSourceAvailability.Disabled, disabled.Availability);
        Assert.DoesNotContain("super-secret-token", disabled.AvailabilityReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", disabled.SourceId, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA123456", disabled.SourceId, StringComparison.Ordinal);
        Assert.DoesNotContain("mysecret", disabled.SourceId, StringComparison.Ordinal);

        var unavailable = await provider.GetCapabilitiesAsync(new WarehouseSourceCapabilityRequest
        {
            ProviderName = DataProviderNames.Databricks,
            SourceId = "dbx-1",
            RuntimeReachable = false,
            UnavailableReason = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig rejected",
        });

        Assert.Equal(WarehouseSourceAvailability.Unavailable, unavailable.Availability);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", unavailable.AvailabilityReason ?? string.Empty, StringComparison.Ordinal);

        var json = disabled.ToJson();
        Assert.Contains("\"availability\":\"Disabled\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", json, StringComparison.Ordinal);
    }
}
