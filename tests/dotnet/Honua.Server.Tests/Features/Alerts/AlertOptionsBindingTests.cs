// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Configuration-binding regression tests for the operator-facing alert worker switch (#3055).
/// </summary>
public sealed class AlertOptionsBindingTests
{
    [UnitTest]
    public void Bind_EnabledTrue_SetsEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alerts:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName));

        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<IOptions<AlertOptions>>().Value.Enabled);
    }
}
