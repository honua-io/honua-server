// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Abstractions;
using Honua.Server.Features.Infrastructure.OpenData;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OpenData;

public sealed class OpenDataServiceCollectionTests
{
    [UnitTest]
    public void AddOpenDataPublication_WhenCapabilityDisabledWithoutDurableStore_RegistersFallbackStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = "duckdb",
                ["OpenData:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenDataPublication(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IOpenDataStore>());
    }

    [UnitTest]
    public void AddOpenDataPublication_WhenCapabilityEnabledWithoutStore_ThrowsStartupError()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = "duckdb",
                ["OpenData:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenDataPublication(configuration));
        Assert.Contains("OpenData:Enabled=true", exception.Message, StringComparison.Ordinal);
    }
}
