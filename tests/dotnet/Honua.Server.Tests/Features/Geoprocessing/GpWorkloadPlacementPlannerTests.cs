// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GpWorkloadPlacementPlannerTests
{
    [UnitTest]
    public void Select_MixedWorkloads_ModestNativeJobUsesLocalGdalLane()
    {
        var result = Select(
            [Local(runtimeProfiles: "managed,native"), Remote()],
            NativeResources());

        result.Workload!.WorkloadId.Should().Be("gp-local");
        result.Decision!.ReasonCode.Should().Be("gp:low-latency-local");
        result.Decision.RuntimeProfile.Should().Be(RuntimeProfiles.Native);
        result.Decision.Resources.EphemeralGib.Should().Be(50);
    }

    [UnitTest]
    public void Select_MixedWorkloads_LargeJobOffloadsToAwsBatch()
    {
        var result = Select(
            [Local(), Remote()],
            NativeResources() with { MemoryMib = 16_384, EphemeralGib = 150 });

        result.Workload!.WorkloadId.Should().Be("gp-aws");
        result.Decision!.ReasonCode.Should().Be("gp:resource-threshold-offload");
        result.Decision.FallbackApplied.Should().BeFalse();
    }

    [UnitTest]
    public void Select_RemoteOnly_ModestJobUsesExplicitRemoteFallback()
    {
        var result = Select([Remote()], NativeResources());

        result.Workload!.WorkloadId.Should().Be("gp-aws");
        result.Decision!.ReasonCode.Should().Be("gp:remote-fallback");
        result.Decision.FallbackApplied.Should().BeTrue();
    }

    [UnitTest]
    public void Select_LocalCapacityPressure_OffloadsWithoutChangingJobIdentity()
    {
        var local = Local(parameters: new Dictionary<string, string>
        {
            [GpWorkloadPlacementParameterKeys.Capacity] = "pressured",
        });

        var result = Select([local, Remote()], NativeResources());

        result.Workload!.WorkloadId.Should().Be("gp-aws");
        result.Decision!.ReasonCode.Should().Be("gp:remote-fallback");
        result.Decision.Reason.Should().Contain("Preferred execution was unavailable");
    }

    [UnitTest]
    public void Select_RemoteDisabled_UsesConfiguredLocalFallbackForLargeJob()
    {
        var options = new GpWorkloadPlacementOptions
        {
            RemoteExecutionEnabled = false,
            AllowLocalFallback = true,
        };

        var result = Select(
            [Local(), Remote()],
            NativeResources() with { Vcpus = 16 },
            options: options);

        result.Workload!.WorkloadId.Should().Be("gp-local");
        result.Decision!.ReasonCode.Should().Be("gp:local-fallback");
    }

    [UnitTest]
    public void Select_ForcedRemote_DoesNotFallBackWhenRemoteUnavailable()
    {
        var act = () => Select(
            [Local()],
            NativeResources(),
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "remote",
            });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .Where(exception => exception.PolicyRef == "gp:no-compatible-workload");
    }

    [UnitTest]
    public void Select_LocalBackendCannotClaimRemoteExecutionClass()
    {
        var contradictory = Local(parameters: new Dictionary<string, string>
        {
            [GpWorkloadPlacementParameterKeys.ExecutionClass] = "remote",
        });

        var act = () => Select(
            [contradictory],
            NativeResources(),
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "remote",
            });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*execution class declaration 'remote' contradicts backend 'local'*");
    }

    [UnitTest]
    public void Select_OperatorForcedRemoteCannotBeOverriddenByLocalRequest()
    {
        var act = () => Select(
            [Local(), Remote()],
            NativeResources(),
            options: new GpWorkloadPlacementOptions { ForceRemoteIsolation = true },
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "local",
            });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*conflicts with the operator's forced remote isolation policy*");
    }

    [UnitTest]
    public void Select_WorkloadResourceEnvelopeCannotSatisfyJob_RejectsBeforeSubmission()
    {
        var remote = Remote(new Dictionary<string, string>
        {
            [GpWorkloadPlacementParameterKeys.MaxMemoryMib] = "4096",
        });

        var act = () => Select(
            [remote],
            NativeResources() with { MemoryMib = 8192 },
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "remote",
            });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*requested memory MiB 8192 exceeds declared maximum 4096*");
    }

    [UnitTest]
    public void Select_WorkloadRuntimeProfileCannotSatisfyJob_RejectsBeforeSubmission()
    {
        var act = () => Select(
            [Local(runtimeProfiles: RuntimeProfiles.Managed)],
            NativeResources());

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*runtime profile 'native' is not declared*");
    }

    [UnitTest]
    public void Select_WorkloadWithoutRuntimeDeclaration_DefaultsToManagedOnly()
    {
        var workload = Workload(
            "gp-legacy-local",
            "local",
            BatchComputeTargetKind.KubernetesJob,
            runtimeProfiles: string.Empty,
            parameters: null);

        var act = () => Select([workload], NativeResources());

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*runtime profile 'native' is not declared*");
    }

    [UnitTest]
    public void Select_S3AffinityPrefersAwsOverKubernetes()
    {
        var result = Select(
            [Remote(), RemoteKubernetes()],
            NativeResources(),
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Affinity] = "s3",
            });

        result.Workload!.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        result.Decision!.ReasonCode.Should().Be("gp:object-store-affinity");
    }

    [UnitTest]
    public void Select_KubernetesBackendCannotMaterializeGpuRequest_RejectsBeforeSubmission()
    {
        var act = () => Select(
            [RemoteKubernetes()],
            NativeResources() with { GpuCount = 1 },
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "remote",
            });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*Kubernetes execution backend cannot materialize a positive GPU resource request*");
    }

    [UnitTest]
    public void Select_ImplicitLocalCannotSilentlyAcceptGpuRequest()
    {
        var act = () => GpWorkloadPlacementPlanner.SelectImplicitLocal(
            RuntimeProfiles.Native,
            NativeResources() with { GpuCount = 1 },
            new Dictionary<string, string>(),
            rasterDecision: null,
            new GpWorkloadPlacementOptions());

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*local execution lane does not declare compatible GPU capacity*");
    }

    [UnitTest]
    public void Select_ExplicitArchitectureRequiresCompatibleLaneDeclaration()
    {
        var act = () => Select(
            [Local(), Remote()],
            NativeResources() with { Arch = "arm64" });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage("*architecture 'arm64' is not declared*");
    }

    [UnitTest]
    public void Select_AwsBlankTierPlaceholders_FallsBackToConfiguredSingleArn()
    {
        var result = Select(
            [Remote(new Dictionary<string, string>
            {
                ["batch.job_definition_arn"] = "arn:aws:batch:region:account:job-definition/gp:1",
                ["batch.job_definition_arn.s"] = "",
                ["batch.job_definition_arn.m"] = " ",
                ["batch.job_definition_arn.l"] = "",
                ["batch.job_definition_arn.xl"] = "",
            })],
            NativeResources());

        result.Workload!.WorkloadId.Should().Be("gp-aws");
    }

    [UnitTest]
    public void Select_AzureFixedPoolWithoutResourceDeclarations_RejectsBeforeSubmission()
    {
        var azure = Workload(
            "gp-azure",
            "honua-azure-batch",
            BatchComputeTargetKind.AzureBatch,
            "managed,native",
            null);

        var act = () => Select(
            [azure],
            NativeResources() with { GpuCount = 1 },
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "remote",
            });

        act.Should().Throw<GeoprocessingAdmissionException>()
            .WithMessage(
                "*placement.max_vcpus*placement.max_memory_mib*placement.max_gpu_count*placement.max_ephemeral_gib*");
    }

    [UnitTest]
    public void Select_PressuredLocalOptionCannotBypassDisabledLocalFallback()
    {
        var local = Local(parameters: new Dictionary<string, string>
        {
            [GpWorkloadPlacementParameterKeys.Capacity] = "pressured",
        });
        var options = new GpWorkloadPlacementOptions
        {
            AllowLocalFallback = false,
            AllowPressuredLocalFallback = true,
        };

        var act = () => Select(
            [local],
            NativeResources() with { Vcpus = 16 },
            options: options);

        act.Should().Throw<GeoprocessingAdmissionException>()
            .Where(exception => exception.PolicyRef == "gp:no-compatible-workload");
    }

    [UnitTest]
    public void Select_PressuredLocalFinalChoicePersistsFallbackDecision()
    {
        var local = Local(parameters: new Dictionary<string, string>
        {
            [GpWorkloadPlacementParameterKeys.Capacity] = "pressured",
        });
        var options = new GpWorkloadPlacementOptions
        {
            AllowPressuredLocalFallback = true,
        };

        var result = Select([local], NativeResources(), options);

        result.Workload!.WorkloadId.Should().Be("gp-local");
        result.Decision!.FallbackApplied.Should().BeTrue();
        result.Decision.ReasonCode.Should().Be("gp:local-fallback");
        result.Decision.Reason.Should().Contain("Preferred execution was unavailable");
    }

    [UnitTest]
    public void Select_RasterRemoteDecisionWithBackendRequestPinsExactBackend()
    {
        var raster = RemoteRasterDecision("preliminary-backend");

        var result = Select(
            [Remote(), RemoteKubernetes()],
            NativeResources(),
            requestParameters: new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Backend] = "honua-kubernetes-job",
            },
            rasterDecision: raster);

        result.Workload!.Backend.Should().Be("honua-kubernetes-job");
        result.Decision!.ReasonCode.Should().Be("gp:raster-pinned-remote");
    }

    [UnitTest]
    public void Select_RasterRemoteDecisionDoesNotPinPreliminaryBackend()
    {
        var raster = RemoteRasterDecision("unavailable-preliminary-backend");

        var result = Select(
            [Remote(), RemoteKubernetes()],
            NativeResources(),
            rasterDecision: raster);

        result.Workload!.Backend.Should().Be("honua-aws-batch");
        result.Decision!.ReasonCode.Should().Be("gp:raster-pinned-remote");
    }

    [UnitTest]
    public void SelectImplicitLocal_ResourceOffloadPreferenceUsesExplicitLocalFallback()
    {
        var result = GpWorkloadPlacementPlanner.SelectImplicitLocal(
            RuntimeProfiles.Native,
            NativeResources() with { Vcpus = 16 },
            new Dictionary<string, string>(),
            rasterDecision: null,
            new GpWorkloadPlacementOptions());

        result.Workload.Should().BeNull();
        result.Decision!.WorkloadId.Should().BeNull();
        result.Decision.Backend.Should().Be(LocalBatchComputeBackend.BackendId);
        result.Decision.ReasonCode.Should().Be("gp:local-fallback");
        result.Decision.FallbackApplied.Should().BeTrue();
    }

    [UnitTest]
    public void SelectImplicitLocal_InvalidPlacementModeIsRejected()
    {
        var act = () => GpWorkloadPlacementPlanner.SelectImplicitLocal(
            RuntimeProfiles.Native,
            NativeResources(),
            new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.Mode] = "somewhere",
            },
            rasterDecision: null,
            new GpWorkloadPlacementOptions());

        act.Should().Throw<GeoprocessingValidationException>();
    }

    private static RasterExecutionDecision RemoteRasterDecision(string? backend) => new()
    {
        ProcessId = "raster.clip",
        Engine = RasterEngine.GdalNative,
        Placement = RasterExecutionPlacement.RemoteBackend,
        Backend = backend,
        InputResidencies = [RasterInputResidency.ObjectStoreCog],
        OutputSink = RasterOutputSink.JobArtifact,
        Cost = new RasterCostEstimate
        {
            ProcessId = "raster.clip",
            Engine = RasterEngine.GdalNative,
            SourceCount = 1,
            BandCount = 1,
            ZoneCount = 0,
            InputPixels = 1,
            OutputPixels = 1,
            DecodedBytes = 8,
            ExpectedScratchBytes = 16,
            ExpectedDatabaseWork = 1,
            UnknownInputs = [],
            RequestExecutionAllowed = false,
        },
        SemanticVersion = "raster-semantics-v1",
        ImplementationVersion = "gdal-native-v1",
        ReasonCode = "raster:object-store-affinity",
        Reason = "Remote raster lane selected.",
        ConfigurationVersion = "raster-v1",
        PolicyRef = "raster-default",
        HealthVersion = "health-v1",
    };

    private static GpWorkloadPlacementResult Select(
        IReadOnlyList<ExecutionJobDefinition> workloads,
        GpResourceProfile resources,
        GpWorkloadPlacementOptions? options = null,
        IReadOnlyDictionary<string, string>? requestParameters = null,
        RasterExecutionDecision? rasterDecision = null)
    {
        var backends = workloads
            .Where(workload => !string.Equals(workload.Backend, "local", StringComparison.Ordinal))
            .Select(workload => Backend(workload.Backend, workload.TargetKind))
            .ToArray();

        return GpWorkloadPlacementPlanner.Select(
            workloads,
            backends,
            localQueueAvailable: true,
            RuntimeProfiles.Native,
            resources,
            requestParameters ?? new Dictionary<string, string>(),
            rasterDecision,
            options ?? new GpWorkloadPlacementOptions());
    }

    private static ExecutionJobDefinition Local(
        string runtimeProfiles = "managed,native,raster-postgis",
        IReadOnlyDictionary<string, string>? parameters = null)
        => Workload(
            "gp-local",
            "local",
            BatchComputeTargetKind.KubernetesJob,
            runtimeProfiles,
            parameters);

    private static ExecutionJobDefinition Remote(IReadOnlyDictionary<string, string>? parameters = null)
        => Workload(
            "gp-aws",
            "honua-aws-batch",
            BatchComputeTargetKind.AwsBatch,
            "managed,native",
            parameters);

    private static ExecutionJobDefinition RemoteKubernetes()
        => Workload(
            "gp-k8s",
            "honua-kubernetes-job",
            BatchComputeTargetKind.KubernetesJob,
            "managed,native",
            null);

    private static ExecutionJobDefinition Workload(
        string id,
        string backend,
        BatchComputeTargetKind target,
        string runtimeProfiles,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var declarations = parameters is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(parameters);
        declarations.TryAdd(GpWorkloadPlacementParameterKeys.RuntimeProfiles, runtimeProfiles);

        return new ExecutionJobDefinition
        {
            WorkloadId = id,
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = target,
            Backend = backend,
            WorkloadName = id,
            Parameters = declarations,
        };
    }

    private static IBatchComputeBackend Backend(string name, BatchComputeTargetKind target)
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(name);
        backend.TargetKind.Returns(target);
        return backend;
    }

    private static GpResourceProfile NativeResources() => new()
    {
        Vcpus = 2,
        MemoryMib = 4096,
        GpuCount = 0,
        TimeoutSeconds = 1800,
        RetryAttempts = 1,
        EphemeralGib = 50,
        Arch = null,
    };
}
