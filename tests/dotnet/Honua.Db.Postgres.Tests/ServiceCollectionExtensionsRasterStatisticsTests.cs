// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Db.Postgres.Tests;

public sealed class ServiceCollectionExtensionsRasterStatisticsTests
{
    [UnitTheory]
    [InlineData(null, PostgresRasterStatisticsOptions.MaximumDenseSampleSlots)]
    [InlineData(1, 1)]
    [InlineData(1024, 1024)]
    [InlineData(PostgresRasterStatisticsOptions.MaximumDenseSampleSlots, PostgresRasterStatisticsOptions.MaximumDenseSampleSlots)]
    public void RasterStatistics_ValidBudget_StartsWithConfiguredValue(int? configured, int expected)
    {
        using var provider = CreateProvider(configured);

        provider.GetRequiredService<IStartupValidator>().Validate();

        provider.GetRequiredService<IOptions<PostgresRasterStatisticsOptions>>().Value
            .MaxDenseSampleSlots.Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(PostgresRasterStatisticsOptions.MaximumDenseSampleSlots + 1)]
    [InlineData(int.MaxValue)]
    public void RasterStatistics_InvalidBudget_IsRejectedAtStartup(int configured)
    {
        using var provider = CreateProvider(configured);

        Action start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .WithMessage("*Database:RasterStatistics:MaxDenseSampleSlots must be between 1 and 16777216.*");
    }

    private static ServiceProvider CreateProvider(int? maximumSlots)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua"
        };
        if (maximumSlots.HasValue)
        {
            settings[$"{PostgresRasterStatisticsOptions.SectionName}:MaxDenseSampleSlots"] =
                maximumSlots.Value.ToString(CultureInfo.InvariantCulture);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);
        return services.BuildServiceProvider();
    }
}
