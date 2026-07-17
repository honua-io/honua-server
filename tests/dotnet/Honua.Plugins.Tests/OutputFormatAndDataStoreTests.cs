// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.Sample.UtilityValidationPlugin;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Plugins.Tests;

/// <summary>
/// Tests for the data-store and output-format plugin extension points (issue #2856, ADR-0066).
/// </summary>
public sealed class OutputFormatAndDataStoreTests
{
    private static ServiceProvider BuildProvider(
        HonuaEdition edition,
        Action<IHonuaPluginBuilder>? configure,
        bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition));
        services.AddSingleton<IAuditLog, NullAuditLog>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plugins:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        services.AddHonuaPlugins(config, configure);
        return services.BuildServiceProvider();
    }

    // ---- Output format ----------------------------------------------------------------------

    [Fact]
    public void OutputFormatRegistry_Resolves_LicensedPluginFormat()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<FeatureLinesOutputFormatPlugin>());

        var registry = provider.GetRequiredService<IFeatureOutputFormatRegistry>();
        registry.HasFormats.Should().BeTrue();
        registry.AdvertisedFormats.Should().ContainSingle().Which.FormatId.Should().Be("featurelines");

        registry.TryGetFormat("FEATURELINES", out var format).Should().BeTrue();
        format!.MediaType.Should().Be("application/x-ndjson");
    }

    [Fact]
    public void OutputFormatRegistry_IsInert_WhenUnlicensed()
    {
        using var provider = BuildProvider(HonuaEdition.Community, p => p.Add<FeatureLinesOutputFormatPlugin>());

        var registry = provider.GetRequiredService<IFeatureOutputFormatRegistry>();
        registry.HasFormats.Should().BeFalse();
        registry.AdvertisedFormats.Should().BeEmpty();
        registry.TryGetFormat("featurelines", out _).Should().BeFalse();
    }

    [Fact]
    public void OutputFormatRegistry_IsInert_WhenKillSwitchOff()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<FeatureLinesOutputFormatPlugin>(), enabled: false);

        provider.GetRequiredService<IFeatureOutputFormatRegistry>().HasFormats.Should().BeFalse();
    }

    [Fact]
    public void OutputFormatRegistry_IsNoOp_WhenNoFormatPlugins()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, configure: null);

        var registry = provider.GetRequiredService<IFeatureOutputFormatRegistry>();
        registry.Should().BeSameAs(NoOpFeatureOutputFormatRegistry.Instance);
        registry.HasFormats.Should().BeFalse();
    }

    [Fact]
    public void OutputFormatPlugin_WithoutCapability_FailsFast()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p.Add<UncappedOutputFormatPlugin>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OutputFormats capability*");
    }

    [Fact]
    public void OutputFormatRegistry_Rejects_ReservedBuiltInToken()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p.Add<ReservedTokenOutputFormatPlugin>())
            .GetRequiredService<IFeatureOutputFormatRegistry>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*reserved format id 'csv'*");
    }

    [Fact]
    public async Task OutputFormatPlugin_Writes_NewlineDelimitedJson()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<FeatureLinesOutputFormatPlugin>());
        var registry = provider.GetRequiredService<IFeatureOutputFormatRegistry>();
        registry.TryGetFormat("featurelines", out var format).Should().BeTrue();

        var context = new FeatureOutputFormatContext(
            "svc", 1, "Layer",
            [new FeatureOutputField("name", "String", true)],
            4326);

        using var stream = new MemoryStream();
        var written = await format!.WriteAsync(TwoFeatures(), context, stream, CancellationToken.None);

        written.Should().Be(2);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"name\":\"Alpha\"").And.Contain("\"id\":1");
    }

    private static async IAsyncEnumerable<Feature> TwoFeatures()
    {
        yield return Feature.Create(1, geometry: null, ImmutableDictionary<string, object?>.Empty.SetItem("name", "Alpha"));
        yield return Feature.Create(2, geometry: null, ImmutableDictionary<string, object?>.Empty.SetItem("name", "Beta"));
        await Task.CompletedTask;
    }

    // ---- Data store -------------------------------------------------------------------------

    [Fact]
    public void DataStorePlugin_IsRegistered_AsFeatureDataProvider()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<ReadOnlyVectorSourcePlugin>());

        // The plugin is registered as an additional IFeatureDataProvider, so the same registry the
        // server composes (from GetServices<IFeatureDataProvider>()) routes to it by provider name —
        // no router changes.
        var registry = new FeatureDataProviderRegistry(provider.GetServices<IFeatureDataProvider>());

        registry.TryGetProvider(ReadOnlyVectorSourcePlugin.Name, out var resolved).Should().BeTrue();
        resolved.ProviderName.Should().Be(ReadOnlyVectorSourcePlugin.Name);
        resolved.Writer.Should().BeNull("the contributed source is read-only");

        var catalog = provider.GetRequiredService<PluginCatalog>();
        catalog.Plugins.Should().ContainSingle()
            .Which.ProvidesDataStore.Should().BeTrue();
    }

    [Fact]
    public void DataStorePlugin_WithoutCapability_FailsFast()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p.Add<UncappedDataStorePlugin>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DataStore capability*");
    }
}
