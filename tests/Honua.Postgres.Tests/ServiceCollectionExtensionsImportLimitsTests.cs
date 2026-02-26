// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Postgres.Tests;

public sealed class ServiceCollectionExtensionsImportLimitsTests
{
    [Fact]
    public void AddPostgreSqlServices_WithNegativeImportLimits_FallsBackToDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua;Password=test",
                ["Import:Limits:BatchSize"] = "-1",
                ["Import:Limits:MaxMemoryBytes"] = "-2",
                ["Import:Limits:BackgroundJobThresholdBytes"] = "-3",
                ["Import:Limits:MaxPreviewSizeBytes"] = "-4",
                ["Import:Limits:MaxPreviewFeatures"] = "-5",
                ["Import:Limits:StreamBufferSize"] = "-6",
                ["Import:Limits:MaxFeaturesPerFile"] = "-7",
                ["Import:Limits:MaxArchiveEntryBytes"] = "-8",
                ["Import:Limits:MaxArchiveExtractedBytes"] = "-9",
                ["Import:Limits:MaxArchiveCompressionRatio"] = "-10"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration);

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<ImportLimits>();
        var defaults = new ImportLimits();

        limits.BatchSize.Should().Be(defaults.BatchSize);
        limits.MaxMemoryBytes.Should().Be(defaults.MaxMemoryBytes);
        limits.BackgroundJobThresholdBytes.Should().Be(defaults.BackgroundJobThresholdBytes);
        limits.MaxPreviewSizeBytes.Should().Be(defaults.MaxPreviewSizeBytes);
        limits.MaxPreviewFeatures.Should().Be(defaults.MaxPreviewFeatures);
        limits.StreamBufferSize.Should().Be(defaults.StreamBufferSize);
        limits.MaxFeaturesPerFile.Should().Be(defaults.MaxFeaturesPerFile);
        limits.MaxArchiveEntryBytes.Should().Be(defaults.MaxArchiveEntryBytes);
        limits.MaxArchiveExtractedBytes.Should().Be(defaults.MaxArchiveExtractedBytes);
        limits.MaxArchiveCompressionRatio.Should().Be(defaults.MaxArchiveCompressionRatio);
    }

    [Fact]
    public void AddPostgreSqlServices_WithValidImportLimits_UsesConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua;Password=test",
                ["Import:Limits:BatchSize"] = "250",
                ["Import:Limits:MaxMemoryBytes"] = "2048",
                ["Import:Limits:BackgroundJobThresholdBytes"] = "4096",
                ["Import:Limits:MaxPreviewSizeBytes"] = "1024",
                ["Import:Limits:MaxPreviewFeatures"] = "15",
                ["Import:Limits:StreamBufferSize"] = "512",
                ["Import:Limits:MaxFeaturesPerFile"] = "99",
                ["Import:Limits:MaxArchiveEntryBytes"] = "10000",
                ["Import:Limits:MaxArchiveExtractedBytes"] = "20000",
                ["Import:Limits:MaxArchiveCompressionRatio"] = "75.5"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration);

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<ImportLimits>();

        limits.BatchSize.Should().Be(250);
        limits.MaxMemoryBytes.Should().Be(2048);
        limits.BackgroundJobThresholdBytes.Should().Be(4096);
        limits.MaxPreviewSizeBytes.Should().Be(1024);
        limits.MaxPreviewFeatures.Should().Be(15);
        limits.StreamBufferSize.Should().Be(512);
        limits.MaxFeaturesPerFile.Should().Be(99);
        limits.MaxArchiveEntryBytes.Should().Be(10000);
        limits.MaxArchiveExtractedBytes.Should().Be(20000);
        limits.MaxArchiveCompressionRatio.Should().Be(75.5);
    }
}
