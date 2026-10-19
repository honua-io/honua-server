// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Import;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportSubmissionReadinessTests
{
    [Fact]
    public async Task UnregisteredCompositionIsNotReadyAndDoesNotResolveSpeculativeDependencies()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<DatasetImportSubmissionService>(_ => throw new InvalidOperationException("Must not construct submission just to discover readiness."));
        using var services = collection.BuildServiceProvider();
        Assert.False(DatasetImportSubmissionService.IsConfigured(services));
        Assert.False(await DatasetImportSubmissionService.IsReadyAsync(services, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationOnlyIsInsufficientUntilExplicitHostReadinessDecision(bool ready)
    {
        var collection = new ServiceCollection();
        var readiness = new HostReadiness(ready);
        collection.AddSingleton<IDatasetImportSubmissionReadiness>(readiness);
        foreach (var type in new[]
        {
            typeof(DatasetImportSubmissionService), typeof(IImportJobService), typeof(BackgroundImportRunnerDecision),
            typeof(ImportUploadCoordinator), typeof(IDatasetImportAdmissionProvider), typeof(IDatasetImportAuthorityDescriptorReader),
            typeof(IExecutionJobStore), typeof(IJobExecutionLeaseAuthority), typeof(IJobExecutionLeasePolicy),
            typeof(IJobExecutionLeaseObservationPolicy), typeof(IJobExecutionLeaseScopeProvider)
        })
            collection.AddSingleton(type, _ => throw new InvalidOperationException("Discovery must not construct dependencies."));
        using var services = collection.BuildServiceProvider();
        Assert.True(DatasetImportSubmissionService.IsConfigured(services));
        Assert.Equal(0, readiness.Calls);
        Assert.Equal(ready, await DatasetImportSubmissionService.IsReadyAsync(services, default));
        Assert.Equal(1, readiness.Calls);
    }

    private sealed class HostReadiness(bool ready) : IDatasetImportSubmissionReadiness
    {
        internal int Calls { get; private set; }
        public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ready);
        }
    }
}
