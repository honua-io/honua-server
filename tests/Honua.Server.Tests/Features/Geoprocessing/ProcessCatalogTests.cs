// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the built-in process catalog and plan validator.
/// </summary>
[Protocol(Protocols.Grpc)]
public sealed class ProcessCatalogTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    // -----------------------------------------------------------------------
    // Catalog — non-empty and discoverable
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_ListProcesses_ReturnsExactly14BuiltIns()
    {
        var all = _catalog.ListProcesses();

        all.Should().HaveCount(14);
        all.Select(p => p.ProcessId).Should().OnlyHaveUniqueItems();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GeometryCategory_Returns10Processes()
    {
        var geometry = _catalog.GetProcessesByCategory("geometry");

        geometry.Should().HaveCount(10);
        geometry.Should().AllSatisfy(p => p.Category.Should().Be("geometry"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AnalyticsCategory_Returns4Processes()
    {
        var analytics = _catalog.GetProcessesByCategory("analytics");

        analytics.Should().HaveCount(4);
        analytics.Should().AllSatisfy(p => p.Category.Should().Be("analytics"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcess_ReturnsDefinitionForKnownId()
    {
        var buffer = _catalog.GetProcess("geometry.buffer");

        buffer.Should().NotBeNull();
        buffer!.ProcessId.Should().Be("geometry.buffer");
        buffer.Title.Should().Be("Buffer");
        buffer.Category.Should().Be("geometry");
        buffer.Parameters.Should().NotBeEmpty();
        buffer.OutputArtifactKinds.Should().Contain(ArtifactKind.FeatureLayer);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcess_ReturnsNullForUnknownId()
    {
        var result = _catalog.GetProcess("nonexistent.process");

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcessesByCategory_ReturnsEmptyForUnknownCategory()
    {
        var result = _catalog.GetProcessesByCategory("nonexistent");

        result.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AllProcesses_HaveRequiredFields()
    {
        var all = _catalog.ListProcesses();

        all.Should().AllSatisfy(p =>
        {
            p.ProcessId.Should().NotBeNullOrWhiteSpace();
            p.Title.Should().NotBeNullOrWhiteSpace();
            p.Description.Should().NotBeNullOrWhiteSpace();
            p.Category.Should().NotBeNullOrWhiteSpace();
            p.Parameters.Should().NotBeNull();
            p.OutputArtifactKinds.Should().NotBeEmpty();
        });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_AllParameters_HaveRequiredFields()
    {
        var all = _catalog.ListProcesses();

        all.SelectMany(p => p.Parameters).Should().AllSatisfy(param =>
        {
            param.Name.Should().NotBeNullOrWhiteSpace();
            param.DisplayName.Should().NotBeNullOrWhiteSpace();
            param.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_EachExpectedProcess_IsDiscoverable()
    {
        string[] expectedIds =
        [
            "geometry.buffer", "geometry.simplify", "geometry.project",
            "geometry.make-valid", "geometry.union", "geometry.intersect",
            "geometry.clip", "geometry.difference", "geometry.area",
            "geometry.length", "analytics.cluster", "analytics.spatial-join",
            "analytics.buffer-aggregate", "analytics.density"
        ];

        foreach (var processId in expectedIds)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                $"process '{processId}' must be registered in the built-in catalog");
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_MeasurementProcesses_ProduceScalarArtifacts()
    {
        _catalog.GetProcess("geometry.area")!.OutputArtifactKinds
            .Should().Contain(ArtifactKind.Scalar);

        _catalog.GetProcess("geometry.length")!.OutputArtifactKinds
            .Should().Contain(ArtifactKind.Scalar);
    }

    // -----------------------------------------------------------------------
    // Validator — process resolution
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_KnownProcess_WithRequiredParams_ProducesNoViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_UnknownProcess_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "unknown.process"
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "UNKNOWN_PROCESS");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_MissingProcessId_OnGeoprocessStep_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = null
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "MISSING_PROCESS_ID");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_MissingRequiredParameter_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().Contain(v => v.Code == "MISSING_REQUIRED_PARAMETER");
        violations.Where(v => v.Code == "MISSING_REQUIRED_PARAMETER")
            .Should().HaveCount(2, "srid and distance are both required");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_NonGeoprocessSteps_AreSkipped()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.QueryFeatures
                },
                new AnalysisPlanStep
                {
                    StepId = "s2",
                    Kind = AnalysisPlanStepKind.RenderMap
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_UnknownInputKey_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100",
                        ["distnace"] = "200"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v => v.Code == "UNKNOWN_PARAMETER");
        violations.Single(v => v.Code == "UNKNOWN_PARAMETER")
            .FieldPath.Should().Be("steps[s1].inputs.distnace");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_OptionalParameters_DoNotProduceViolations()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "4326",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty("geodesic is optional and should not cause violations");
    }

    // -----------------------------------------------------------------------
    // Validator — typed value validation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryBuffer_InvalidTypedInputs_ProducesViolationPerField()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "not-base64!!",
                        ["srid"] = "abc",
                        ["distance"] = "not-a-number",
                        ["geodesic"] = "maybe"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        var invalid = violations.Where(v => v.Code == "INVALID_PARAMETER_VALUE").ToList();
        invalid.Should().HaveCount(4, "each typed input should report its own INVALID_PARAMETER_VALUE");
        invalid.Select(v => v.FieldPath).Should().BeEquivalentTo(
        [
            "steps[s1].inputs.wkb",
            "steps[s1].inputs.srid",
            "steps[s1].inputs.distance",
            "steps[s1].inputs.geodesic"
        ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_AnalyticsCluster_InvalidTypedInputs_ProducesViolationPerField()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "analytics.cluster",
                    Inputs = new Dictionary<string, string>
                    {
                        ["layerId"] = "   ",
                        ["algorithm"] = "DbScan",
                        ["eps"] = "NaN",
                        ["minPoints"] = "5.5",
                        ["returnHullPerCluster"] = "yes"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        var invalid = violations.Where(v => v.Code == "INVALID_PARAMETER_VALUE").ToList();
        invalid.Select(v => v.FieldPath).Should().BeEquivalentTo(
        [
            "steps[s1].inputs.layerId",
            "steps[s1].inputs.eps",
            "steps[s1].inputs.minPoints",
            "steps[s1].inputs.returnHullPerCluster"
        ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryBuffer_SridZero_IsRejected()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.buffer",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkb"] = "AAAA",
                        ["srid"] = "0",
                        ["distance"] = "100"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.srid");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryUnion_WkbArray_AcceptsJsonArrayOfBase64()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.union",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkbs"] = "[\"AAAA\",\"BBBB\"]",
                        ["srid"] = "4326"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Validator_GeometryUnion_MalformedWkbArray_ProducesViolation()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "geometry.union",
                    Inputs = new Dictionary<string, string>
                    {
                        ["wkbs"] = "AAAA,BBBB",
                        ["srid"] = "4326"
                    }
                }
            ]
        };

        var (violations, _) = ProcessPlanValidator.Validate(plan, _catalog);

        violations.Should().ContainSingle(v =>
            v.Code == "INVALID_PARAMETER_VALUE" &&
            v.FieldPath == "steps[s1].inputs.wkbs");
    }

    // -----------------------------------------------------------------------
    // Catalog — immutability contract
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_ListProcesses_CannotBeCastToMutableArray()
    {
        var all = _catalog.ListProcesses();

        (all is ProcessDefinition[]).Should().BeFalse(
            "read-only catalog must not leak the underlying array through IReadOnlyList");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public void Catalog_GetProcessesByCategory_CannotBeCastToMutableArray()
    {
        var geometry = _catalog.GetProcessesByCategory("geometry");

        (geometry is ProcessDefinition[]).Should().BeFalse(
            "read-only catalog must not leak the underlying array through IReadOnlyList");
    }

    // -----------------------------------------------------------------------
    // Integration: ValidatePlan RPC with catalog validation
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithUnknownProcessId_ReturnsNotExecutable()
    {
        var sut = CreateServiceWithCatalog();

        var plan = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };
        plan.Steps.Add(new Proto.AnalysisPlanStep
        {
            StepId = "step-1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "nonexistent.op"
        });

        var response = await sut.ValidatePlan(
            new Proto.ValidatePlanRequest { Plan = plan },
            CreateCallContext());

        response.IsExecutable.Should().BeFalse();
        response.Violations.Should().Contain(v => v.Code == "UNKNOWN_PROCESS");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithKnownProcess_MissingRequiredParam_ReturnsNotExecutable()
    {
        var sut = CreateServiceWithCatalog();

        var plan = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };
        plan.Steps.Add(new Proto.AnalysisPlanStep
        {
            StepId = "step-1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "geometry.buffer"
        });

        var response = await sut.ValidatePlan(
            new Proto.ValidatePlanRequest { Plan = plan },
            CreateCallContext());

        response.IsExecutable.Should().BeFalse();
        response.Violations.Should().Contain(v => v.Code == "MISSING_REQUIRED_PARAMETER");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private HonuaProcessService CreateServiceWithCatalog()
    {
        var authEval = Substitute.For<IOperatorAuthorizationEvaluator>();
        authEval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(AccessDecision.Allowed());

        var approvalEval = Substitute.For<IOperatorApprovalEvaluator>();
        approvalEval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        var jobService = new GeoprocessingJobService(
            Substitute.For<IUniversalProgressStore>(),
            [Substitute.For<IJobCancellationNotifier>()],
            authEval,
            approvalEval,
            _catalog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GeoprocessingJobService>.Instance);

        return new HonuaProcessService(
            jobService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaProcessService>.Instance);
    }

    private static TestServerCallContext CreateCallContext()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")], "Test"))
        };

        var ctx = new TestServerCallContext();
        ctx.UserState["__HttpContext"] = httpContext;
        return ctx;
    }

    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Metadata _responseTrailers = new();

        public void Dispose() => _cts.Dispose();

        protected override string MethodCore => "/geospatial.v1.ProcessService/ValidatePlan";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
