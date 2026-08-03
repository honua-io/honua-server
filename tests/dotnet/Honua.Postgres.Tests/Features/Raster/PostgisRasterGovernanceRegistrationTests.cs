// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Raster;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgisRasterGovernanceRegistrationTests
{
    [Fact]
    public void DefaultPostgresComposition_DoesNotRegisterRasterWorkerGovernance()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Host=serving-db;Database=honua;Username=web_role;Password=test",
        });
        var services = new ServiceCollection();

        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, configuration);

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(PostgisRasterDataSource));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IPostgisRasterExecutionSessionFactory));
    }

    [Fact]
    public void Registration_CreatesMarkerPoolWithoutServingDataSourceFallback()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RasterPostgis"] =
                "Host=raster-db;Database=honua;Username=raster_role;Password=test;MaxPoolSize=99",
            ["Geoprocessing:Raster:Postgis:RequiredRole"] = "raster_role",
            ["Geoprocessing:Raster:Postgis:MaxConcurrency"] = "3",
            ["Geoprocessing:Raster:Postgis:MaxConcurrencyPerTenant"] = "1",
        });
        var services = new ServiceCollection();

        services.AddPostgisRasterExecutionGovernance(configuration);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(PostgisRasterDataSource));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));
        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<PostgisRasterDataSource>();
        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        connection.Host.Should().Be("raster-db");
        connection.Username.Should().Be("raster_role");
        connection.MaxPoolSize.Should().Be(3);
        connection.MinPoolSize.Should().Be(0);
        connection.Multiplexing.Should().BeFalse();
        connection.NoResetOnClose.Should().BeFalse();
        connection.ApplicationName.Should().Be("honua-raster-postgis-worker");
        provider.GetRequiredService<IPostgisRasterExecutionSessionFactory>()
            .Should().BeOfType<PostgisRasterExecutionSessionFactory>();
    }

    [Fact]
    public void Registration_MissingDedicatedConnectionString_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddPostgisRasterExecutionGovernance(Configuration([]));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<PostgisRasterDataSource>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:RasterPostgis*");
    }

    [Fact]
    public void OptionsValidator_RejectsLooserTenantPolicyAndInvalidTimeout()
    {
        var options = new PostgisRasterExecutionOptions
        {
            QueueTimeout = TimeSpan.Zero,
            Tenants = new Dictionary<string, PostgisRasterTenantPolicy>
            {
                ["tenant-a"] = new()
                {
                    WorkLimits = new PostgisRasterTenantWorkLimits
                    {
                        MaxSourceCount = 17,
                    },
                },
            },
        };

        var result = new PostgisRasterExecutionOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("QueueTimeout", StringComparison.Ordinal));
        result.Failures.Should().Contain(failure => failure.Contains("only tighten", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsBinding_ConfiguresPerTenantConcurrencyAndWork()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RasterPostgis"] =
                "Host=raster-db;Database=honua;Username=raster_role;Password=test",
            ["Geoprocessing:Raster:Postgis:RequiredRole"] = "raster_role",
            ["Geoprocessing:Raster:Postgis:Tenants:tenant-a:MaxConcurrency"] = "1",
            ["Geoprocessing:Raster:Postgis:Tenants:tenant-a:WorkLimits:MaxInputPixels"] = "1000",
        });
        var services = new ServiceCollection();
        services.AddPostgisRasterExecutionGovernance(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<PostgisRasterExecutionOptions>>().Value;

        options.Tenants["tenant-a"].MaxConcurrency.Should().Be(1);
        options.Tenants["tenant-a"].WorkLimits!.MaxInputPixels.Should().Be(1000);
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
