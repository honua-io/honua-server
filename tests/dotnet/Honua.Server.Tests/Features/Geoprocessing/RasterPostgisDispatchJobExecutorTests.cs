// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.ControlPlane;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Protocol(TestProtocols.GPServer)]
public sealed class RasterPostgisDispatchJobExecutorTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void Executor_AcceptsOnlyRasterPostgisProfile()
    {
        var sut = CreateExecutor(new FakeProviderExecutor(Capability()));

        sut.AcceptedRuntimeProfiles.Should().Equal(RuntimeProfiles.RasterPostgis);
        RuntimeProfiles.CanClaim(sut.AcceptedRuntimeProfiles, RuntimeProfiles.RasterPostgis).Should().BeTrue();
        RuntimeProfiles.CanClaim(sut.AcceptedRuntimeProfiles, RuntimeProfiles.Managed).Should().BeFalse();
        RuntimeProfiles.CanClaim(sut.AcceptedRuntimeProfiles, RuntimeProfiles.Native).Should().BeFalse();
        RuntimeProfiles.CanClaim(sut.AcceptedRuntimeProfiles, RuntimeProfiles.CustomCode).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ExecuteAsync_ExactPinnedRoute_PassesTenantAttemptAndCancellationAndPublishesReference()
    {
        RasterProviderExecutionRequest? captured = null;
        CancellationToken capturedToken = default;
        var provider = new FakeProviderExecutor(
            Capability(),
            (request, cancellationToken) =>
            {
                captured = request;
                capturedToken = cancellationToken;
                return Task.FromResult(RasterProviderExecutionResult.Succeeded(
                [
                    new RasterProviderResultReference
                    {
                        Reference = "honua://artifacts/raster-result-1",
                        MediaType = "image/tiff",
                        Sha256 = new string('a', 64),
                        Length = 1024,
                    },
                ]));
            });
        var sut = CreateExecutor(provider);
        var context = Substitute.For<IJobExecutionContext>();
        context.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        var job = Job();

        var result = await sut.ExecuteAsync(job, context, cts.Token);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        captured.Should().NotBeNull();
        captured!.OperationId.Should().Be("job-postgis-1");
        captured.Attempt.Should().Be(2);
        captured.TenantId.Should().Be("tenant-a");
        captured.Decision.Should().BeSameAs(job.Spec.RasterExecution);
        captured.Parameters.Should().NotBeSameAs(job.Spec.Parameters);
        capturedToken.Should().Be(cts.Token);
        await context.Received(1).PublishArtifactAsync(
            "honua://artifacts/raster-result-1",
            cts.Token);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ExecuteAsync_ProviderCancellation_PropagatesToDurableJobSubstrate()
    {
        var provider = new FakeProviderExecutor(
            Capability(),
            (_, cancellationToken) =>
                Task.FromCanceled<RasterProviderExecutionResult>(cancellationToken));
        var sut = CreateExecutor(provider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.ExecuteAsync(
            Job(),
            Substitute.For<IJobExecutionContext>(),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.ExecutionCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ExecuteAsync_MissingExactSemanticRoute_FailsCapabilityUnavailable()
    {
        var provider = new FakeProviderExecutor(Capability() with
        {
            Variant = Capability().Variant with { ImplementationVersion = "different@1.0.0" },
        });
        var sut = CreateExecutor(provider);

        var result = await sut.ExecuteAsync(
            Job(),
            Substitute.For<IJobExecutionContext>(),
            CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("capability-unavailable");
        provider.ExecutionCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ExecuteAsync_MissingPinnedTenant_FailsBeforeProvider()
    {
        var provider = new FakeProviderExecutor(Capability());
        var sut = CreateExecutor(provider);
        var job = Job() with
        {
            Spec = Job().Spec with
            {
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "raster.clip",
                },
            },
        };

        var result = await sut.ExecuteAsync(
            job,
            Substitute.For<IJobExecutionContext>(),
            CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("tenant-fence-missing");
        provider.ExecutionCount.Should().Be(0);
    }

    private static RasterPostgisDispatchJobExecutor CreateExecutor(
        params IRasterProviderExecutor[] executors) => new(
            executors,
            NullLogger<RasterPostgisDispatchJobExecutor>.Instance);

    private static ExecutionJobRecord Job()
    {
        var decision = new RasterExecutionDecision
        {
            ProcessId = "raster.clip",
            Engine = RasterEngine.Postgis,
            ProviderId = "postgis",
            ProviderPolicyVersion = "postgis-raster-v1",
            Placement = RasterExecutionPlacement.DurablePostgis,
            InputResidencies = [RasterInputResidency.Postgis],
            OutputSink = RasterOutputSink.JobArtifact,
            Cost = new RasterCostEstimate
            {
                ProcessId = "raster.clip",
                Engine = RasterEngine.Postgis,
                SourceCount = 1,
                BandCount = 1,
                ZoneCount = 0,
                InputPixels = 256,
                OutputPixels = 256,
                DecodedBytes = 1024,
                ExpectedScratchBytes = 1024,
                ExpectedDatabaseWork = 256,
                UnknownInputs = [],
                RequestExecutionAllowed = false,
            },
            SemanticVersion = "1.0.0",
            ImplementationVersion = "honua.postgis.raster.clip@1.0.0",
            ReasonCode = "postgis-source-local",
            Reason = "test",
            PolicyRef = "raster-default",
            ConfigurationVersion = "raster-execution-v1",
            HealthVersion = "health-v1",
        };
        return new ExecutionJobRecord
        {
            OperationId = "job-postgis-1",
            Status = ExecutionJobStatus.Running,
            AttemptCount = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "raster-postgis",
                RuntimeProfile = RuntimeProfiles.RasterPostgis,
                RasterExecution = decision,
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "raster.clip",
                    [RasterProviderExecutionParameterKeys.TenantId] = "tenant-a",
                },
            },
        };
    }

    private static RasterProviderCapability Capability() => new()
    {
        ProviderId = "postgis",
        Engine = RasterEngine.Postgis,
        Variant = new RasterSemanticVariant
        {
            ProcessId = "raster.clip",
            SemanticVersion = "1.0.0",
            ImplementationVersion = "honua.postgis.raster.clip@1.0.0",
        },
        PolicyVersion = "postgis-raster-v1",
        Availability = RasterProviderAvailability.Available,
    };

    private sealed class FakeProviderExecutor : IRasterProviderExecutor
    {
        private readonly Func<RasterProviderExecutionRequest, CancellationToken,
            Task<RasterProviderExecutionResult>> _execute;

        public FakeProviderExecutor(
            RasterProviderCapability capability,
            Func<RasterProviderExecutionRequest, CancellationToken,
                Task<RasterProviderExecutionResult>>? execute = null)
        {
            Capabilities = [capability];
            _execute = execute ?? ((_, _) => throw new InvalidOperationException("Unexpected execution."));
        }

        public IReadOnlyList<RasterProviderCapability> Capabilities { get; }

        public int ExecutionCount { get; private set; }

        public Task<RasterProviderExecutionResult> ExecuteAsync(
            RasterProviderExecutionRequest request,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return _execute(request, cancellationToken);
        }
    }
}
