// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class WorkspaceCleanupServiceTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledTick_HonorsAutomaticCleanupOption(bool enabled)
    {
        var lifecycle = Substitute.For<IWorkspaceLifecycleService>();
        lifecycle.RunCleanupAsync(Arg.Any<CancellationToken>()).Returns(new CleanupResult());
        var services = new ServiceCollection();
        services.AddSingleton(lifecycle);
        using var provider = services.BuildServiceProvider();
        using var service = new WorkspaceCleanupService(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkspaceOptions { EnableAutomaticCleanup = enabled }),
            NullLogger<WorkspaceCleanupService>.Instance);
        var handler = new WorkspaceCleanupScheduledTickHandler(service);
        using var cancellation = new CancellationTokenSource();
        await handler.RunTickAsync(cancellation.Token);
        await lifecycle.Received(enabled ? 1 : 0).RunCleanupAsync(cancellation.Token);
    }
}
