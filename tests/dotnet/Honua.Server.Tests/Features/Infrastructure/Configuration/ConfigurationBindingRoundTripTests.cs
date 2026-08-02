// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Core.Configuration;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.Federation;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Configuration;

/// <summary>
/// Representative source-generated binding round trips for nested objects, nullable children,
/// lists, and dictionaries in the configuration graphs audited by honua-server#3055.
/// </summary>
public sealed class ConfigurationBindingRoundTripTests
{
    [UnitTest]
    public void AlertOptions_NestedValues_AreBound()
    {
        var configuration = BuildConfiguration(
            ("Alerts:Enabled", "true"),
            ("Alerts:Evaluation:WorkerName", "binding-probe"),
            ("Alerts:Dispatch:Digest:MaxBatchSize", "17"),
            ("Alerts:Ops:Channels:0", "slack"));
        var services = new ServiceCollection();
        services.AddOptions<AlertOptions>().Bind(configuration.GetSection(AlertOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AlertOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal("binding-probe", options.Evaluation.WorkerName);
        Assert.Equal(17, options.Dispatch.Digest.MaxBatchSize);
        Assert.Equal(["slack"], options.Ops.Channels);
    }

    [UnitTest]
    public void AlertDeliveryOptions_NullableChannelAndLeaves_AreBound()
    {
        var configuration = BuildConfiguration(
            ("Alerts:Dispatch:Email:SmtpHost", "smtp.example.test"),
            ("Alerts:Dispatch:Email:SmtpPort", "2525"),
            ("Alerts:Dispatch:Email:UseSsl", "false"));
        var services = new ServiceCollection();
        services.AddOptions<AlertDeliveryOptions>()
            .Bind(configuration.GetSection(AlertDeliveryOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var email = provider.GetRequiredService<IOptions<AlertDeliveryOptions>>().Value.Dispatch.Email;

        Assert.NotNull(email);
        Assert.Equal("smtp.example.test", email.SmtpHost);
        Assert.Equal(2525, email.SmtpPort);
        Assert.False(email.UseSsl);
    }

    [UnitTest]
    public void FederationOptions_ListEntry_AreBound()
    {
        var configuration = BuildConfiguration(
            ("Federation:Sources:0:Id", "warehouse"),
            ("Federation:Sources:0:DisplayName", "Remote warehouse"),
            ("Federation:Sources:0:Kind", "OgcWfs"),
            ("Federation:Sources:0:Endpoint", "https://example.test/ogc"),
            ("Federation:Sources:0:RemoteLayer", "parcels"),
            ("Federation:Sources:0:RequestTimeoutSeconds", "45"));
        var services = new ServiceCollection();
        services.AddOptions<FederationSourceOptions>()
            .Bind(configuration.GetSection(FederationSourceOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var source = Assert.Single(
            provider.GetRequiredService<IOptions<FederationSourceOptions>>().Value.Sources);

        Assert.Equal("warehouse", source.Id);
        Assert.Equal("Remote warehouse", source.DisplayName);
        Assert.Equal(FederatedSourceKind.OgcWfs, source.Kind);
        Assert.Equal("https://example.test/ogc", source.Endpoint);
        Assert.Equal("parcels", source.RemoteLayer);
        Assert.Equal(45, source.RequestTimeoutSeconds);
    }

    [UnitTest]
    public void LimitsAndTileOptions_NestedObjectAndDictionary_AreBound()
    {
        var configuration = BuildConfiguration(
            ("Limits:Validation:MaxVertices", "321"),
            ("Limits:Imports:BatchSize", "42"),
            ("TileOptions:Eviction:Enabled", "true"),
            ("TileOptions:Eviction:MaxEntries", "1000"),
            ("TileOptions:TilesetLifecycle:roads:TtlSeconds", "120"));
        var services = new ServiceCollection();
        services.AddOptions<LimitsOptions>()
            .Bind(configuration.GetSection(LimitsOptions.SectionName));
        services.AddOptions<TileOptions>()
            .Bind(configuration.GetSection(TileOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var tiles = provider.GetRequiredService<IOptions<TileOptions>>().Value;

        Assert.Equal(321, limits.Validation.MaxVertices);
        Assert.Equal(42, limits.Imports.BatchSize);
        Assert.True(tiles.Eviction.Enabled);
        Assert.Equal(1000, tiles.Eviction.MaxEntries);
        Assert.NotNull(tiles.TilesetLifecycle);
        Assert.Equal(120, tiles.TilesetLifecycle["roads"].TtlSeconds);
    }

    private static IConfigurationRoot BuildConfiguration(
        params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => setting.Value))
            .Build();
}
