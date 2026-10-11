// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit coverage for identity-keyed geoprocessing admission (honua-server#5625, SRV-GP-002).
/// </summary>
public sealed class GeoprocessingAdmissionIdentityTests
{
    [UnitTest]
    public void ResolveRatePrincipal_ScopesOwnerByTenant_AndFailsClosed()
    {
        ExecutionAdmissionIdentity.ResolveRatePrincipal("owner-1", "tenant-a")
            .Should().Be("tenant:tenant-a:owner-1");
        ExecutionAdmissionIdentity.ResolveRatePrincipal("owner-1", null)
            .Should().Be("owner-1");
        ExecutionAdmissionIdentity.ResolveRatePrincipal(null, "tenant-a")
            .Should().Be("tenant:tenant-a:unbound");

        var act = () => ExecutionAdmissionIdentity.ResolveRatePrincipal("  ", " ");
        act.Should().Throw<GeoprocessingAdmissionException>()
            .Which.PolicyRef.Should().Be("execution.admission.identity");
    }

    [UnitTest]
    public async Task EnsureAdmitted_SameOwnerDifferentTenants_DoNotShareRateBucket()
    {
        var dispatcher = Dispatcher();

        var first = await dispatcher.EnsureAdmittedAsync("owner-1", "tenant-a", 1, OperationPriority.Normal, CancellationToken.None);
        var otherTenant = await dispatcher.EnsureAdmittedAsync("owner-1", "tenant-b", 1, OperationPriority.Normal, CancellationToken.None);

        first!.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
        otherTenant!.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);

        var again = () => dispatcher.EnsureAdmittedAsync("owner-1", "tenant-a", 1, OperationPriority.Normal, CancellationToken.None);
        var throttled = await again.Should().ThrowAsync<GeoprocessingAdmissionException>();
        throttled.Which.Outcome.Should().Be(ExecutionAdmissionOutcome.Throttled);
        throttled.Which.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Rate);
    }

    [UnitTest]
    public async Task EnsureAdmitted_MissingOwner_UsesTenantBucket_AndRejectsWhenBothMissing()
    {
        var dispatcher = Dispatcher();

        var first = await dispatcher.EnsureAdmittedAsync(null, "tenant-a", 1, OperationPriority.Normal, CancellationToken.None);
        first!.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);

        var again = () => dispatcher.EnsureAdmittedAsync(null, "tenant-a", 1, OperationPriority.Normal, CancellationToken.None);
        (await again.Should().ThrowAsync<GeoprocessingAdmissionException>())
            .Which.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Rate);

        var missing = () => dispatcher.EnsureAdmittedAsync(null, null, 1, OperationPriority.Normal, CancellationToken.None);
        var denied = await missing.Should().ThrowAsync<GeoprocessingAdmissionException>();
        denied.Which.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        denied.Which.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Rate);
        denied.Which.PolicyRef.Should().Be("execution.admission.identity");
    }

    private static GeoprocessingJobDispatcher Dispatcher()
    {
        var options = new ExecutionAdmissionOptions
        {
            Enabled = true,
            MaxSubmissionsPerWindow = 1,
            MaxConcurrentJobsGlobal = 0,
            MaxConcurrentJobsPerPartition = 0,
            MaxCostWeightPerPartition = 0,
        };
        var admission = Substitute.For<IOptionsMonitor<ExecutionAdmissionOptions>>();
        admission.CurrentValue.Returns(options);
        var evaluator = new ExecutionAdmissionEvaluator(
            admission,
            TimeProvider.System,
            NullLogger<ExecutionAdmissionEvaluator>.Instance);
        var executor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        executor.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        return new GeoprocessingJobDispatcher(
            NullLogger<GeoprocessingJobService>.Instance,
            executor,
            Substitute.For<IUniversalProgressStore>(),
            admissionEvaluator: evaluator);
    }
}
