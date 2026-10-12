// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Db.Postgres.Features.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Tests;

public sealed class ServiceCollectionExtensionsImportLimitsTests
{
    [Fact]
    public void AddPostgreSqlServices_RegistersSingletonOperationLogWithoutCapturingScopedProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=honua_test;Username=honua;Password=test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        services.Single(descriptor =>
                descriptor.ServiceType == typeof(ISavedMapOperationLogRepository))
            .Lifetime.Should().Be(ServiceLifetime.Singleton);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<ISavedMapOperationLogRepository>()
            .Should().NotBeNull();
    }

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
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

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
                ["Import:Limits:MaxArchiveCompressionRatio"] = "75.5",
                ["Import:Limits:GeometryValidityMode"] = "strict"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

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
        // GeometryValidityMode is parsed case-insensitively from the section (#2743).
        limits.GeometryValidityMode.Should().Be(Honua.Core.Configuration.ValidationMode.Strict);
    }

    [Fact]
    public void AddPostgreSqlServices_WithoutGeometryValidityMode_FallsBackToDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua;Password=test",
                ["Import:Limits:BatchSize"] = "250"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<ImportLimits>();

        // Unset (or unparseable) GeometryValidityMode keeps the ImportLimits default (Repair).
        limits.GeometryValidityMode.Should().Be(new ImportLimits().GeometryValidityMode);
        limits.GeometryValidityMode.Should().Be(Honua.Core.Configuration.ValidationMode.Repair);
    }

    /// <summary>
    /// Mechanical drift guard for the hand-rolled <c>Import:Limits</c> parser (#3315): every
    /// settable property of <see cref="ImportLimits"/> gets a distinct non-default value in
    /// configuration, and every one of them must survive into the registered instance. Six keys
    /// (MaxVertices, MaxRings, MaxWkbSize, MaxSingleFeatureBytes, ValidateGeometry,
    /// SkipInvalidGeometry) were declared on the record but never read by the parser, so operators
    /// who set them silently kept the defaults. Reflection-driven on purpose: adding a property to
    /// the record without teaching the parser fails here, naming the property.
    /// </summary>
    [Fact]
    public void AddPostgreSqlServices_HonoursEveryImportLimitsConfigurationKey()
    {
        var defaults = new ImportLimits();
        var properties = typeof(ImportLimits)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        properties.Should().NotBeEmpty();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Host=localhost;Database=honua_test;Username=honua;Password=test"
        };
        var expected = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var value = NonDefaultValueFor(property, defaults);
            expected[property.Name] = value;
            settings[$"Import:Limits:{property.Name}"] = FormatConfigurationValue(value);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<ImportLimits>();

        var ignored = properties
            .Where(property => !Equals(property.GetValue(limits), expected[property.Name]))
            .Select(property =>
                $"{property.Name} (configured {FormatConfigurationValue(expected[property.Name])}, " +
                $"got {FormatConfigurationValue(property.GetValue(limits))})")
            .ToArray();

        ignored.Should().BeEmpty(
            "every Import:Limits:* key must reach ImportLimits; the hand parser in " +
            "ServiceCollectionExtensions silently ignores any property it never assigns (#3315)");
    }

    private static object NonDefaultValueFor(PropertyInfo property, ImportLimits defaults)
    {
        var current = property.GetValue(defaults);
        var type = property.PropertyType;

        if (type == typeof(bool))
        {
            return !(bool)current!;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type)
                .Cast<object>()
                .First(candidate => !Equals(candidate, current));
        }

        if (type == typeof(int))
        {
            // Every int limit is parsed as positive (or non-negative for MaxFeaturesPerFile),
            // so offsetting the default keeps the value acceptable to the parser.
            return (int)current! + 7;
        }

        if (type == typeof(long))
        {
            return (long)current! + 7L;
        }

        if (type == typeof(double))
        {
            return (double)current! + 0.5d;
        }

        throw new InvalidOperationException(
            $"ImportLimits.{property.Name} has unsupported type {type} — teach this guard how to " +
            "produce a non-default value for it rather than dropping the property from coverage.");
    }

    private static string FormatConfigurationValue(object? value) => value switch
    {
        null => string.Empty,
        Enum enumValue => enumValue.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    [Fact]
    public void AddPostgreSqlServices_RegistersMigrationRunCatalog()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua_test;Username=honua;Password=test"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMigrationRunCatalog>()
            .Should().BeOfType<PostgresMigrationRunCatalog>();
    }
}
