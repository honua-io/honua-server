// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.LocalRunner;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.LocalRunner;

/// <summary>
/// Proves the GP Devkit native-profile fidelity contract (issue #2180): the
/// <see cref="ExecutionJobSpec"/> the local runner builds for a single
/// <c>(processId, inputs)</c> invocation is IDENTICAL to the spec the production
/// submit path (<c>GeoprocessingJobService.BuildSpec</c> → the no-registered-workload
/// case) produces for the equivalent single-step plan — so <c>gp run</c>/<c>gp plan</c>
/// is a true dry-run of the real submit spec, not a parallel representation.
///
/// <para>
/// Both paths route through the single source of truth
/// (<see cref="GeoprocessingSpecBuilder"/>): the submit path calls
/// <see cref="GeoprocessingSpecBuilder.BuildNoWorkloadSpec"/> for the no-workload case,
/// and the local runner's <see cref="GeoprocessingLocalRunner.BuildJobRecord"/> models
/// its invocation as the same single-step plan and calls the same builder. These tests
/// pin that equivalence so a future divergence (a parameter key, the runtime-profile
/// stamp, or an envelope field drifting on one path) fails loudly.
/// </para>
/// </summary>
public sealed class GeoprocessingLocalRunnerSpecEquivalenceTests
{
    [UnitTest]
    public void BuildJobRecord_NativeProcess_MatchesSubmitPathSpec()
    {
        const string processId = "gdal.hillshade";
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "ZmFrZS1yYXN0ZXI=",
            ["azimuth"] = "315",
            ["altitude"] = "45",
        };

        var runnerSpec = GeoprocessingLocalRunner
            .BuildJobRecord(processId, inputs, new NativeFakeExecutor(processId))
            .Spec;

        // The submit path's no-registered-workload branch builds its spec through the
        // same shared builder from the equivalent single-step plan, stamping `native`
        // for a gdal.* step (catalog-driven in prod; executor-accepted-set-driven here,
        // which resolves to the same value).
        var plan = GeoprocessingSpecBuilder.SingleStepPlan(processId, inputs, planId: runnerSpec.WorkloadName.Split(':')[^1]);
        var submitSpec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal),
            requiredRuntimeProfile: RuntimeProfiles.Native);

        AssertSpecsEquivalent(runnerSpec, submitSpec, expectedProfile: RuntimeProfiles.Native);
    }

    [UnitTest]
    public void BuildJobRecord_ManagedProcess_MatchesSubmitPathSpec_AndLeavesProfileNull()
    {
        const string processId = "geometry.buffer";
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["srid"] = "4326",
            ["distance"] = "10",
        };

        var runnerSpec = GeoprocessingLocalRunner
            .BuildJobRecord(processId, inputs, new ManagedFakeExecutor(processId))
            .Spec;

        var plan = GeoprocessingSpecBuilder.SingleStepPlan(processId, inputs, planId: runnerSpec.WorkloadName.Split(':')[^1]);
        var submitSpec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal),
            requiredRuntimeProfile: null);

        // A managed op leaves RuntimeProfile null/default exactly as the submit path
        // does (it only stamps a non-managed profile), so the lean dispatcher claims it.
        runnerSpec.RuntimeProfile.Should().BeNull();
        AssertSpecsEquivalent(runnerSpec, submitSpec, expectedProfile: null);
    }

    [UnitTest]
    public void BuildJobRecord_ProjectsCanonicalDurableKeys_TheWorkerResolvesProcessIdFrom()
    {
        const string processId = "gdal.ogr2ogr";
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "Zm9v",
            ["sourceFormat"] = "GeoJSON",
            ["targetFormat"] = "CSV",
        };

        var spec = GeoprocessingLocalRunner
            .BuildJobRecord(processId, inputs, new NativeFakeExecutor(processId))
            .Spec;

        // The canonical process-definitions key the dispatcher/executors resolve on.
        spec.Parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions]
            .Should().Be(processId);

        // Step-0 inputs under the canonical prefix.
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        spec.Parameters[prefix + "source"].Should().Be("Zm9v");
        spec.Parameters[prefix + "sourceFormat"].Should().Be("GeoJSON");
        spec.Parameters[prefix + "targetFormat"].Should().Be("CSV");

        // The local runner no longer emits the legacy `protocolProcessId` fallback —
        // prod's spec relies on process_definitions, and so does the dry-run, keeping
        // the two specs identical.
        spec.Parameters.Should().NotContainKey("protocolProcessId");
    }

    private static void AssertSpecsEquivalent(
        ExecutionJobSpec runnerSpec,
        ExecutionJobSpec submitSpec,
        string? expectedProfile)
    {
        runnerSpec.Kind.Should().Be(submitSpec.Kind);
        runnerSpec.TargetKind.Should().Be(submitSpec.TargetKind);
        runnerSpec.Backend.Should().Be(submitSpec.Backend);
        runnerSpec.WorkloadName.Should().Be(submitSpec.WorkloadName);
        runnerSpec.RuntimeProfile.Should().Be(expectedProfile);
        runnerSpec.RuntimeProfile.Should().Be(submitSpec.RuntimeProfile);

        // The durable parameter bag — the only payload a worker dispatches on — must
        // match key-for-key and value-for-value.
        runnerSpec.Parameters.Should().BeEquivalentTo(submitSpec.Parameters);
    }

    private sealed class NativeFakeExecutor(string processId) : IProcessExecutor
    {
        private static readonly IReadOnlySet<string> Native =
            new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

        public IReadOnlySet<string> ProcessIds { get; } =
            new HashSet<string>(StringComparer.Ordinal) { processId };

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public IReadOnlySet<string> AcceptedRuntimeProfiles => Native;

        public Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(JobExecutionResult.Succeeded());
    }

    private sealed class ManagedFakeExecutor(string processId) : IProcessExecutor
    {
        public IReadOnlySet<string> ProcessIds { get; } =
            new HashSet<string>(StringComparer.Ordinal) { processId };

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        // Managed executors fall back to the managed-only default accepted set.
        public IReadOnlySet<string> AcceptedRuntimeProfiles => RuntimeProfiles.DefaultAccepted;

        public Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(JobExecutionResult.Succeeded());
    }
}
