// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertsServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddAlerts_WithDefaultConfiguration_LeavesBufferedChannelsUnsupported()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAlerts(configuration);

        using var provider = services.BuildServiceProvider();
        var sinks = provider.GetServices<IAlertDeliverySink>().ToDictionary(static sink => sink.ChannelType);

        sinks[AlertChannelType.WebSocket].Should().BeOfType<UnsupportedAlertDeliverySink>();
        sinks[AlertChannelType.Digest].Should().BeOfType<UnsupportedAlertDeliverySink>();
        provider.GetService<IAlertNotificationBroadcaster>().Should().BeNull();
    }
}
