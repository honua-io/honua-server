// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentManagedProcessContractTests
{
    [UnitTest]
    public void InlineBuffer_AcceptsActualNativeShapeWithoutChangingPlanParameters()
    {
        var plan = Plan();
        AgentManagedBufferContract.RequirePlan(plan, new());
        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(plan, new(), null);
        AgentManagedBufferContract.RequireJob(Job(spec), new());
        AgentManagedBufferContract.PlanParametersFingerprint(spec.Parameters).Should().Be(
            AgentManagedBufferContract.PlanParametersFingerprint(GeoprocessingSpecBuilder.ProjectPlanParameters(plan, new())));
    }

    [UnitTheory]
    [InlineData("process")]
    [InlineData("dag")]
    [InlineData("output")]
    [InlineData("dependency")]
    [InlineData("unknown-input")]
    [InlineData("file")]
    [InlineData("network")]
    [InlineData("stream")]
    [InlineData("geographic")]
    [InlineData("negative-distance")]
    [InlineData("nonfinite-distance")]
    [InlineData("excessive-distance")]
    [InlineData("oversized")]
    [InlineData("conflicting-frame")]
    public void UnsupportedPlanOrSource_IsRefused(string mutation)
    {
        var plan = Plan();
        var step = plan.Steps[0];
        var inputs = new Dictionary<string, string>(step.Inputs);
        if (mutation == "process") step = step with { ProcessId = "analytics.buffer-aggregate" };
        if (mutation == "dependency") step = step with { DependsOn = ["foreign"] };
        if (mutation == "unknown-input") inputs["sourcePath"] = "/tmp/private.geojson";
        if (mutation == "file") inputs["input"] = "file:///tmp/private.geojson";
        if (mutation == "network") inputs["input"] = "https://example.invalid/private.geojson";
        if (mutation == "stream") inputs["input"] = "honua://jobs/foreign/features";
        if (mutation == "geographic") inputs["sourceCrs"] = "EPSG:4326";
        if (mutation == "negative-distance") inputs["distance"] = "-1";
        if (mutation == "nonfinite-distance") inputs["distance"] = "NaN";
        if (mutation == "excessive-distance") inputs["distance"] = "1000001";
        if (mutation == "oversized") inputs["input"] = UriFor("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"padding\":\"" + new string('x', (int)AgentManagedBufferContract.MaximumInputBytes) + "\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}}]}");
        if (mutation == "conflicting-frame") inputs["input"] = UriFor("""{"type":"FeatureCollection","honua:spatialReference":{"schemaVersion":"honua.feature-crs/v1","state":"known","srid":4326},"features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[1,2]}}]}""");
        step = step with { Inputs = inputs };
        plan = plan with
        {
            Steps = mutation == "dag" ? [step, step with { StepId = "two" }] : [step],
            Outputs = mutation == "output" ? [ArtifactKind.FeatureLayer, ArtifactKind.Map] : [ArtifactKind.FeatureLayer]
        };

        FluentActions.Invoking(() => AgentManagedBufferContract.RequirePlan(plan, new())).Should().Throw<UnauthorizedAccessException>();
    }

    [UnitTheory]
    [InlineData("native")]
    [InlineData("remote")]
    [InlineData("image")]
    [InlineData("workspace")]
    [InlineData("custom-code")]
    [InlineData("second-step")]
    public void WorkerEnvelope_CannotAddDifferentRuntimeOrDurableSideEffects(string mutation)
    {
        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(Plan(), new(), null);
        var parameters = new Dictionary<string, string>(spec.Parameters);
        if (mutation == "native") spec = spec with { RuntimeProfile = RuntimeProfiles.Native };
        if (mutation == "remote") spec = spec with { Backend = "aws-batch" };
        if (mutation == "image") spec = spec with { Artifact = "unreviewed-image" };
        if (mutation == "workspace") parameters[GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = "new-workspace";
        if (mutation == "custom-code") parameters["customcode.source"] = "unreviewed";
        if (mutation == "second-step") parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "1.input"] = Plan().Steps[0].Inputs["input"];
        spec = spec with { Parameters = parameters };

        FluentActions.Invoking(() => AgentManagedBufferContract.RequireJob(Job(spec), new())).Should().Throw<UnauthorizedAccessException>();
    }

    [UnitTest]
    public void AbsentBinding_IsOmittedEvenByLegacyIntentSerializerOptions()
    {
        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(Plan(), new(), null);
        // This serializer intentionally includes nulls, as the immutable intent
        // serializer does. Adding an optional spec field must not invalidate any
        // already accepted import/job fingerprint when that field is absent.
        var json = JsonSerializer.Serialize(spec, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
        json.Should().NotContain(nameof(ExecutionJobSpec.AgentManagedProcessBinding));
    }

    [UnitTest]
    public void OriginalBinding_IsPartOfImmutableIntentAndCannotBeRetargeted()
    {
        var job = Job(GeoprocessingSpecBuilder.BuildNoWorkloadSpec(Plan(), new(), null));
        var binding = new AgentManagedProcessJobBinding
        {
            Contract = AgentManagedBufferContract.Format,
            WorkspaceId = "original",
            TurnId = "turn",
            TurnHash = new('a', 64),
            TargetId = "original-target",
            ReleaseId = "release",
            WorkspaceOwnerActorId = "human",
            ExecutorActorId = "executor",
            TenantId = "public",
            PolicyContextFingerprint = "v1:sha256:" + new string('b', 64),
            PlanParametersFingerprint = new('c', 64),
            MaximumInputBytes = AgentManagedBufferContract.MaximumInputBytes,
            MaximumOutputBytes = AgentManagedBufferContract.MaximumOutputBytes,
            Sources = []
        };
        var bound = job with { Spec = job.Spec with { AgentManagedProcessBinding = binding } };
        var fingerprint = Honua.Core.Features.Authorization.Domain.JobExecutionLeaseIntentFingerprint.Compute(bound);
        fingerprint.Should().NotBe(Honua.Core.Features.Authorization.Domain.JobExecutionLeaseIntentFingerprint.Compute(job));
        fingerprint.Should().NotBe(Honua.Core.Features.Authorization.Domain.JobExecutionLeaseIntentFingerprint.Compute(
            bound with { Spec = bound.Spec with { AgentManagedProcessBinding = binding with { TargetId = "foreign-target" } } }));
    }

    [UnitTest]
    public async Task Dispatcher_RefusesDifferentContextJobBeforeOpeningAnyAuthorityScope()
    {
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var scopes = Substitute.For<IServiceScopeFactory>();
        var dispatcher = new GeoprocessingDispatchJobExecutor([new ManagedBufferAggregateExecutor(options)],
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance, serviceScopeFactory: scopes);
        var job = Job(GeoprocessingSpecBuilder.BuildNoWorkloadSpec(Plan(), new(), null));
        job = job with
        {
            Audit = new()
            {
                SubmitterSecurityContext = new("owner", "public", [], "role")
                { OwnerActorId = "owner", AgentCredential = new() { Kind = "api-key", Id = Guid.NewGuid().ToString("D"), Permissions = [] } }
            }
        };
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("foreign-job");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.ExecuteAsync(job, context, default));

        scopes.DidNotReceive().CreateScope();
        await context.DidNotReceive().PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    internal static AnalysisPlan Plan() => new()
    {
        PlanId = "native-buffer",
        IntentId = "native-first-shelter",
        Steps = [new() { StepId = "buffer", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = AgentManagedBufferContract.ProcessId,
            Inputs = new Dictionary<string, string> { ["input"] = UriFor("""{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"source_id":"shelter-first"},"geometry":{"type":"Point","coordinates":[1000,2000]}}]}"""),
                ["sourceCrs"] = "EPSG:3857", ["distance"] = "500", ["dissolve"] = "false" } }],
        Outputs = [ArtifactKind.FeatureLayer]
    };

    private static string UriFor(string json) => "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    private static ExecutionJobRecord Job(ExecutionJobSpec spec) => new()
    {
        OperationId = "native-buffer-job",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Status = ExecutionJobStatus.Queued,
        Spec = spec
    };
}
