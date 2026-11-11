// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.WorkflowPackages;
using Honua.Server.Tests.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageSchedulePublicationTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(ProcessParameterValueType.LayerId, "1", "2")]
    [InlineData(ProcessParameterValueType.WholeNumber, "1", "2")]
    [InlineData(ProcessParameterValueType.FloatingPoint, "1.5", "2.5")]
    [InlineData(ProcessParameterValueType.Wkb, "AQEAAAAAAAAAAADwPwAAAAAAAABA", "AQEAAAAAAAAAAAAIQAAAAAAAABBA")]
    public async Task CreateVersion_ForEachTypedInputs_ValidatesConcreteItems(
        ProcessParameterValueType valueType, string first, string second)
    {
        var harness = CreateForEachHarness(valueType);
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Typed fan-out", Graph = ForEachGraph(first, second) }, Author(), CancellationToken.None);

        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);

        Assert.True(version.Validation.IsValid);
        Assert.Equal(new[] { first, second }, harness.Validated.SelectMany(plan => plan.Steps).Select(step => step.Inputs["value"]));
        Assert.Equal(new[] { first, second }, harness.Authorized.SelectMany(plan => plan.Steps).Select(step => step.Inputs["value"]));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(ProcessParameterValueType.LayerId)]
    [InlineData(ProcessParameterValueType.WholeNumber)]
    [InlineData(ProcessParameterValueType.FloatingPoint)]
    [InlineData(ProcessParameterValueType.Wkb)]
    public async Task CreateVersion_ForEachInvalidItem_RejectsConcreteValueWithoutSaving(ProcessParameterValueType valueType)
    {
        var harness = CreateForEachHarness(valueType);
        var valid = valueType == ProcessParameterValueType.Wkb ? "AQEAAAAAAAAAAADwPwAAAAAAAABA" : "1";
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Invalid fan-out", Graph = ForEachGraph(valid, "invalid") }, Author(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() =>
            harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None));

        Assert.Contains(exception.Result.Failures, failure => failure.Code == "INVALID_PARAMETER_VALUE" && failure.FieldPath == "steps[echo::1].inputs.value");
        Assert.Empty(await harness.Packages.ListVersionsAsync(saved.PackageId));
    }

    [UnitTest]
    public async Task CreateVersion_ForEachDeniedItem_RejectsWithoutSaving()
    {
        var harness = CreateForEachHarness(ProcessParameterValueType.LayerId);
        harness.Jobs.EnsurePlanExecutionTierAuthorizedAsync(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<AnalysisPlan>().Steps.Any(step => step.Inputs["value"] == "2")
                ? throw new UnauthorizedAccessException("Denied layer")
                : Task.FromResult(call.Arg<AnalysisPlan>()));
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Denied fan-out", Graph = ForEachGraph("1", "2") }, Author(), CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None));

        Assert.Empty(await harness.Packages.ListVersionsAsync(saved.PackageId));
    }

    [UnitTest]
    public async Task PublishVersion_ForEachTypedInputs_RejectsBackgroundDynamicLayer()
    {
        var harness = CreateForEachHarness(ProcessParameterValueType.LayerId, "layerId");
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Published fan-out", Graph = ForEachGraph("1", "2", "layerId") }, Author(), CancellationToken.None);
        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WorkflowPackageValidationException>(() =>
            harness.Service.PublishVersionAsync(saved.PackageId, version.Version,
                ScheduleRequest("typed-fan-out", true, true, "0 * * * *"), Author(), CancellationToken.None));

        Assert.Contains(exception.Result.Failures, failure => failure.Code == "UNSUPPORTED_DYNAMIC_CATALOG_SCHEDULE");
        Assert.Null(await harness.Packages.GetPublicationAsync("typed-fan-out"));
    }

    [UnitTest]
    public async Task CreateVersion_ForEachDataBinding_ValidatesConcreteInputsAndRemapsDependencies()
    {
        var harness = CreateForEachHarness(ProcessParameterValueType.WholeNumber, "value",
            new ProcessParameterSpec { Name = "iteration", DisplayName = "Iteration", ValueType = ProcessParameterValueType.Text, Required = true, Description = "Iteration" });
        var target = ForEachGraph("east", "west", "iteration").Nodes[0];
        var upstream = target with
        {
            NodeId = "upstream",
            ForEach = null,
            Parameters = new Dictionary<string, string> { ["value"] = "1", ["iteration"] = "start" }
        };
        var graph = new WorkflowGraph
        {
            Nodes = [upstream, target, upstream with { NodeId = "downstream" }],
            Edges =
            [
                new WorkflowEdge { SourceNodeId = "upstream", TargetNodeId = "echo", TargetPort = "value" },
                new WorkflowEdge { SourceNodeId = "echo", TargetNodeId = "downstream", Kind = WorkflowEdgeKind.Control }
            ]
        };
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Bound fan-out", Graph = graph }, Author(), CancellationToken.None);

        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);

        Assert.True(version.Validation.IsValid);
        var steps = Assert.Single(harness.Validated).Steps;
        Assert.Equal(new[] { "upstream", "echo::0", "echo::1", "downstream" }, steps.Select(step => step.StepId));
        Assert.All(steps.Where(step => step.StepId.StartsWith("echo::", StringComparison.Ordinal)), step =>
        {
            Assert.Equal(new[] { "upstream" }, step.DependsOn);
            Assert.Equal("artifact:0", step.Inputs["value"]);
        });
        Assert.Equal(new[] { "east", "west" }, steps.Where(step => step.StepId.StartsWith("echo::", StringComparison.Ordinal)).Select(step => step.Inputs["iteration"]));
        Assert.Equal(new[] { "echo::0", "echo::1" }, steps.Single(step => step.StepId == "downstream").DependsOn);
    }

    [UnitTest]
    public async Task PublishVersion_ForEachStaticLayer_PreservesPublicationBinding()
    {
        var harness = CreateForEachHarness(ProcessParameterValueType.WholeNumber, "value",
            new ProcessParameterSpec { Name = "layerId", DisplayName = "Layer", ValueType = ProcessParameterValueType.LayerId, Required = true, Description = "Static layer" });
        var graph = ForEachGraph("1", "2");
        graph = graph with
        {
            Nodes = [graph.Nodes[0] with
        {
            Parameters = new Dictionary<string, string>(graph.Nodes[0].Parameters) { ["layerId"] = "7" }
        }]
        };
        var saved = await harness.Service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Static layer fan-out", Graph = graph }, Author(), CancellationToken.None);
        var version = await harness.Service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);

        var publication = await harness.Service.PublishVersionAsync(saved.PackageId, version.Version,
            ScheduleRequest("static-layer-fan-out", true, true, "0 * * * *"), Author(), CancellationToken.None);

        var definition = (await harness.Definitions.GetAsync(publication.WorkflowDefinitionId!))!;
        var template = Assert.Single(definition.Steps);
        Assert.NotNull(template.ForEach);
        Assert.Equal("7", Assert.Single(template.Plan.Steps).Inputs["authorized-layer"]);
        Assert.Empty(WorkflowDefinitionValidator.Validate(definition));
    }

    private static WorkflowGraph ForEachGraph(string first, string second, string parameterName = "value")
        => new()
        {
            Nodes = [new WorkflowNode
            {
                NodeId = "echo", NodeTypeId = "process:test.echo",
                Parameters = new Dictionary<string, string> { [parameterName] = "${item}" },
                ForEach = new WorkflowForEachSpec([first, second])
            }]
        };

    private static ForEachHarness CreateForEachHarness(ProcessParameterValueType valueType,
        string parameterName = "value", params ProcessParameterSpec[] additionalParameters)
    {
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.GetProcess("test.echo").Returns(new ProcessDefinition
        {
            ProcessId = "test.echo",
            Title = "Typed echo",
            Description = "Typed echo",
            Category = "test",
            Parameters = [new ProcessParameterSpec { Name = parameterName, DisplayName = "Value", ValueType = valueType, Required = true, Description = "Iteration value" }, .. additionalParameters],
            OutputArtifactKinds = [ArtifactKind.File]
        });
        var packages = new InMemoryWorkflowPackageStore();
        var definitions = new FakeWorkflowDefinitionStore();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var validated = new List<AnalysisPlan>();
        var authorized = new List<AnalysisPlan>();
        jobs.ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>()).Returns(call =>
        {
            var plan = call.Arg<AnalysisPlan>();
            validated.Add(plan);
            var (violations, warnings) = ProcessPlanValidator.Validate(plan, catalog);
            return new PlanValidationResult { IsExecutable = violations.Count == 0, Violations = violations, Warnings = warnings };
        });
        jobs.EnsurePlanExecutionTierAuthorizedAsync(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var plan = call.Arg<AnalysisPlan>();
            authorized.Add(plan);
            Assert.Equal("author", call.Arg<ClaimsPrincipal>().Identity!.Name);
            return Task.FromResult(plan with
            {
                Steps = plan.Steps.Select(step => step with
                {
                    Inputs = new Dictionary<string, string>(step.Inputs)
                    {
                        ["authorized-value"] = step.Inputs[parameterName],
                        ["authorized-layer"] = step.Inputs.GetValueOrDefault("layerId", "none")
                    }
                }).ToArray()
            });
        });
        var service = new WorkflowPackageService(packages, new EchoNodeRegistry(), jobs, TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance, definitions);
        return new ForEachHarness(service, packages, definitions, jobs, validated, authorized);
    }

    private sealed record ForEachHarness(WorkflowPackageService Service, InMemoryWorkflowPackageStore Packages,
        FakeWorkflowDefinitionStore Definitions, IGeoprocessingJobService Jobs,
        List<AnalysisPlan> Validated, List<AnalysisPlan> Authorized);
}
