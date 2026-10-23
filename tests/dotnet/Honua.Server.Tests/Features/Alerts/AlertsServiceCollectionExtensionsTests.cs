// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.Infrastructure.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertsServiceCollectionExtensionsTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(null, false)]
    [InlineData("true", false)]
    [InlineData("true", true)]
    [InlineData("false", true)]
    public void AddAlerts_TenantWorkersEnabled_RejectsBeforeAnyStoreOrReceiverCanBeUsed(
        string? tenantResolution, bool schemaRouting)
    {
        var services = CreateServices(tenantResolution, schemaRouting, alertsEnabled: true, previewEnabled: true);
        using var provider = services.BuildServiceProvider();

        // Execute the host's actual ValidateOnStart hook. No database or sink can be
        // invoked before this rejection; the absence of those dependencies must not
        // be the reason startup fails. Both workers resolve this same options value.
        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle()
            .Which.Should().Be("Alert processing cannot run with tenant resolution or schema routing enabled: " +
                "alert evaluation and delivery stores are instance-wide. Set Alerts:Enabled=false " +
                "on multi-tenant instances; use a separate single-tenant instance for Preview alerts.");

        var resolveWorkerOptions = () => provider.GetRequiredService<IOptions<AlertOptions>>().Value;
        resolveWorkerOptions.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("false", false, true, true, true)]
    [InlineData("true", true, false, true, false)]
    [InlineData(null, false, true, false, false)]
    public void AddAlerts_SafeConfiguration_PreservesExplicitWorkerState(
        string? tenantResolution, bool schemaRouting, bool alertsEnabled, bool previewEnabled, bool expectedEnabled)
    {
        var services = CreateServices(tenantResolution, schemaRouting, alertsEnabled, previewEnabled);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        provider.GetRequiredService<IOptions<AlertOptions>>().Value.Enabled.Should().Be(expectedEnabled);
    }

    private static ServiceCollection CreateServices(
        string? tenantResolution, bool schemaRouting, bool alertsEnabled, bool previewEnabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["Alerts:Enabled"] = alertsEnabled.ToString(),
            ["MultiTenancy:SchemaRouting:Enabled"] = schemaRouting.ToString(),
            ["Capabilities:Experimental:alerts.geofence:Enabled"] = previewEnabled.ToString()
        };
        if (tenantResolution is not null)
        {
            values["MultiTenancy:Enabled"] = tenantResolution;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlerts(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services;
    }

    [UnitTest]
    public void AddAlerts_WithDefaultConfiguration_LeavesBufferedChannelsUnsupported()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAlerts(configuration);

        using var provider = services.BuildServiceProvider();
        var sinks = provider.GetServices<IAlertDeliverySink>().ToDictionary(static sink => sink.ChannelType);

        sinks[AlertChannelType.WebSocket].Should().BeOfType<WebSocketAlertDeliverySink>();
        sinks[AlertChannelType.Digest].Should().BeOfType<UnsupportedAlertDeliverySink>();
        provider.GetService<IAlertNotificationBroadcaster>().Should().NotBeNull()
            .And.BeOfType<InMemoryAlertNotificationBroadcaster>();
    }
}
