// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Regression tests for <see cref="MigrationBatchBackgroundService"/>. The
/// provider-specific <see cref="IMigrationBatchRunCatalog"/> and
/// <see cref="IMigrationBatchOrchestrator"/> are registered only by the
/// infrastructure composition root (the Postgres extension), which is skipped in
/// the Test environment and by primary providers that own no batch-run schema.
/// The hosted service is registered unconditionally, so it must no-op rather than
/// throw an unregistered-service error on every tick (related to #1687).
/// </summary>
[Collection("Unit")]
public sealed class MigrationBatchBackgroundServiceTests
{
    [UnitTest]
    public async Task AdvanceActiveBatches_WhenCatalogNotRegistered_NoOpsInsteadOfThrowing()
    {
        // No IMigrationBatchRunCatalog / IMigrationBatchOrchestrator registered,
        // mirroring a host where the infrastructure composition root was skipped.
        using var provider = new ServiceCollection().BuildServiceProvider();
        var jobManager = Substitute.For<IDistributedImportJobManager>();
        var leaderElection = Substitute.For<IDistributedLeaderElection>();
        leaderElection.InstanceId.Returns("test-instance");
        jobManager.LeaderElection.Returns(leaderElection);

        var service = new MigrationBatchBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            jobManager,
            NullLogger<MigrationBatchBackgroundService>.Instance);

        await InvokeAdvanceActiveBatchesAsync(service);
    }

    private static async Task InvokeAdvanceActiveBatchesAsync(MigrationBatchBackgroundService service)
    {
        var method = typeof(MigrationBatchBackgroundService).GetMethod(
            "AdvanceActiveBatchesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("the advance loop body should exist");

        try
        {
            await (Task)method!.Invoke(service, new object[] { CancellationToken.None })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real failure (e.g. the unregistered-service error this
            // guard prevents) rather than the reflection wrapper.
            throw ex.InnerException;
        }
    }
}
