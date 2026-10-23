// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.WorkflowPackages;
using Honua.Server.Tests.Features.Orchestration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>
/// Schedule publication behavior for issue 5634: a disabled publish must not cron,
/// each publication owns its definition, and a failed write does not leave a trigger.
/// </summary>
public sealed class WorkflowPackageSchedulePublicationTests
{
    [UnitTest]
    public async Task DisabledPublication_DoesNotRegisterAnEnabledCron()
    {
        var harness = CreateHarness();
        var version = await PublishableVersionAsync(harness);

        var publication = await harness.Service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-off", enabled: false, scheduleEnabled: true, cron: "*/5 * * * *"),
            Author(),
            CancellationToken.None);

        var definitionId = WorkflowPackageService.ScheduleWorkflowDefinitionId(
            version.PackageId, version.Version, "pub-off");
        Assert.Equal(WorkflowPublicationStatus.Disabled, publication.Status);
        Assert.Equal(definitionId, publication.WorkflowDefinitionId);
        var definition = await harness.Definitions.GetAsync(definitionId);
        Assert.NotNull(definition);
        Assert.False(definition!.Trigger!.Enabled);
        Assert.DoesNotContain(await harness.Definitions.ListScheduledAsync(), item => item.WorkflowId == definitionId);
    }

    [UnitTest]
    public async Task ScheduleDeclarationDisabled_DoesNotRegisterAnEnabledCron()
    {
        var harness = CreateHarness();
        var version = await PublishableVersionAsync(harness);

        var publication = await harness.Service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-schedule-off", enabled: true, scheduleEnabled: false, cron: "0 0 * * *"),
            Author(),
            CancellationToken.None);

        Assert.Equal(WorkflowPublicationStatus.Active, publication.Status);
        var definition = await harness.Definitions.GetAsync(publication.WorkflowDefinitionId!);
        Assert.NotNull(definition);
        Assert.False(definition!.Trigger!.Enabled);
        Assert.Empty(await harness.Definitions.ListScheduledAsync());
    }

    [UnitTest]
    public async Task TwoSchedulePublications_KeepIndependentDefinitions()
    {
        var harness = CreateHarness();
        var version = await PublishableVersionAsync(harness);

        await harness.Service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-a", enabled: true, scheduleEnabled: true, cron: "0 * * * *"),
            Author(),
            CancellationToken.None);
        await harness.Service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-b", enabled: true, scheduleEnabled: true, cron: "0 0 * * *"),
            Author(),
            CancellationToken.None);

        var idA = WorkflowPackageService.ScheduleWorkflowDefinitionId(version.PackageId, version.Version, "pub-a");
        var idB = WorkflowPackageService.ScheduleWorkflowDefinitionId(version.PackageId, version.Version, "pub-b");
        Assert.NotEqual(idA, idB);
        Assert.Equal("0 * * * *", (await harness.Definitions.GetAsync(idA))!.Trigger!.CronExpression);
        Assert.Equal("0 0 * * *", (await harness.Definitions.GetAsync(idB))!.Trigger!.CronExpression);

        await harness.Service.SetPublicationStatusAsync("pub-a", WorkflowPublicationStatus.Disabled, CancellationToken.None);
        Assert.False((await harness.Definitions.GetAsync(idA))!.Trigger!.Enabled);
        Assert.DoesNotContain(await harness.Definitions.ListScheduledAsync(), item => item.WorkflowId == idA);
        Assert.Contains(await harness.Definitions.ListScheduledAsync(), item => item.WorkflowId == idB);

        await harness.Service.DeletePublicationAsync("pub-b", CancellationToken.None);
        Assert.Null(await harness.Definitions.GetAsync(idB));
        Assert.NotNull(await harness.Definitions.GetAsync(idA));
    }

    [UnitTest]
    public async Task DefinitionWriteFailure_RemovesThePublicationAndTheDefinition()
    {
        var packages = new InMemoryWorkflowPackageStore();
        var definitions = new FailingSetDefinitionStore(new FakeWorkflowDefinitionStore());
        var service = CreateService(packages, definitions);
        var version = await PublishableVersionAsync(service, packages);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-fail", enabled: true, scheduleEnabled: true, cron: "0 0 * * *"),
            Author(),
            CancellationToken.None));

        Assert.Null(await packages.GetPublicationAsync("pub-fail"));
        Assert.Empty(await definitions.ListAsync());
        Assert.Equal(1, definitions.DeleteCalls);
    }

    [UnitTest]
    public async Task Publish_DeletedWhileTheDefinitionIsWritten_DoesNotLeaveACron()
    {
        var packages = new InMemoryWorkflowPackageStore();
        var inner = new FakeWorkflowDefinitionStore();
        var definitions = new HookedDefinitionStore(inner, async (_, _, cancellationToken) =>
        {
            await packages.DeletePublicationAsync("pub-race", cancellationToken);
        });
        var service = CreateService(packages, definitions);
        var version = await PublishableVersionAsync(service, packages);
        var definitionId = WorkflowPackageService.ScheduleWorkflowDefinitionId(
            version.PackageId, version.Version, "pub-race");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-race", enabled: true, scheduleEnabled: true, cron: "0 * * * *"),
            Author(),
            CancellationToken.None));

        Assert.Contains("removed before its schedule could be armed", exception.Message, StringComparison.Ordinal);
        Assert.Null(await packages.GetPublicationAsync("pub-race"));
        Assert.Null(await definitions.GetAsync(definitionId));
        Assert.Empty(await definitions.ListScheduledAsync());
    }

    [UnitTest]
    public async Task Publish_DisabledWhileTheDefinitionIsWritten_DoesNotArmTheCron()
    {
        var packages = new InMemoryWorkflowPackageStore();
        var inner = new FakeWorkflowDefinitionStore();
        var definitions = new HookedDefinitionStore(inner, async (call, _, cancellationToken) =>
        {
            if (call == 1)
            {
                await packages.SetPublicationStatusAsync(
                    "pub-disarm",
                    WorkflowPublicationStatus.Disabled,
                    cancellationToken);
            }
        });
        var service = CreateService(packages, definitions);
        var version = await PublishableVersionAsync(service, packages);

        var publication = await service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-disarm", enabled: true, scheduleEnabled: true, cron: "*/5 * * * *"),
            Author(),
            CancellationToken.None);

        Assert.Equal(WorkflowPublicationStatus.Disabled, publication.Status);
        var definition = await definitions.GetAsync(publication.WorkflowDefinitionId!);
        Assert.NotNull(definition);
        Assert.False(definition!.Trigger!.Enabled);
        Assert.Empty(await definitions.ListScheduledAsync());
    }

    [UnitTest]
    public async Task Publish_DisabledAfterTheTriggerIsArmed_DisarmsTheCron()
    {
        var packages = new InMemoryWorkflowPackageStore();
        var inner = new FakeWorkflowDefinitionStore();
        var definitions = new HookedDefinitionStore(inner, async (call, definition, cancellationToken) =>
        {
            if (call == 2 && definition.Trigger?.Enabled == true)
            {
                await packages.SetPublicationStatusAsync(
                    "pub-late",
                    WorkflowPublicationStatus.Disabled,
                    cancellationToken);
            }
        });
        var service = CreateService(packages, definitions);
        var version = await PublishableVersionAsync(service, packages);

        var publication = await service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-late", enabled: true, scheduleEnabled: true, cron: "0 0 * * *"),
            Author(),
            CancellationToken.None);

        Assert.Equal(WorkflowPublicationStatus.Disabled, publication.Status);
        Assert.False((await definitions.GetAsync(publication.WorkflowDefinitionId!))!.Trigger!.Enabled);
        Assert.Empty(await definitions.ListScheduledAsync());
    }

    [UnitTest]
    public async Task LegacySharedDefinition_DisableOnePublication_KeepsTheOtherScheduled()
    {
        var harness = CreateHarness();
        var version = await PublishableVersionAsync(harness);
        var first = await harness.Service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-a", enabled: true, scheduleEnabled: true, cron: "0 * * * *"),
            Author(),
            CancellationToken.None);
        var shared = await harness.Definitions.GetAsync(first.WorkflowDefinitionId!);
        Assert.NotNull(shared);
        var legacyId = WorkflowPackageService.LegacyScheduleWorkflowDefinitionId(version.PackageId, version.Version);
        await harness.Definitions.SetAsync(shared! with { WorkflowId = legacyId });
        await harness.Definitions.DeleteAsync(first.WorkflowDefinitionId!);
        Assert.True(await harness.Packages.TryReplaceWorkflowDefinitionIdAsync(
            first.PublicationId,
            first.WorkflowDefinitionId!,
            legacyId));
        await harness.Packages.SavePublicationAsync(first with
        {
            PublicationId = "pub-b",
            WorkflowDefinitionId = legacyId,
            Schedule = new WorkflowSchedule { CronExpression = "0 0 * * *", TimeZone = "UTC", Enabled = true },
            CreatedAt = first.CreatedAt.AddMinutes(1)
        });

        await harness.Service.SetPublicationStatusAsync("pub-a", WorkflowPublicationStatus.Disabled, CancellationToken.None);

        var idA = WorkflowPackageService.ScheduleWorkflowDefinitionId(version.PackageId, version.Version, "pub-a");
        var idB = WorkflowPackageService.ScheduleWorkflowDefinitionId(version.PackageId, version.Version, "pub-b");
        Assert.Null(await harness.Definitions.GetAsync(legacyId));
        Assert.Equal(idA, (await harness.Packages.GetPublicationAsync("pub-a"))!.WorkflowDefinitionId);
        Assert.Equal(idB, (await harness.Packages.GetPublicationAsync("pub-b"))!.WorkflowDefinitionId);
        Assert.False((await harness.Definitions.GetAsync(idA))!.Trigger!.Enabled);
        Assert.True((await harness.Definitions.GetAsync(idB))!.Trigger!.Enabled);
        Assert.Equal("0 0 * * *", (await harness.Definitions.GetAsync(idB))!.Trigger!.CronExpression);
        Assert.DoesNotContain(await harness.Definitions.ListScheduledAsync(), item => item.WorkflowId == idA);
        Assert.Contains(await harness.Definitions.ListScheduledAsync(), item => item.WorkflowId == idB);
    }

    [UnitTest]
    public void GraphHash_DistinguishesParameterAndPortBoundaries()
    {
        var parametersA = new WorkflowGraph
        {
            Nodes =
            [
                new WorkflowNode
                {
                    NodeId = "area",
                    NodeTypeId = "process:geometry.area",
                    Parameters = new Dictionary<string, string> { ["a"] = "b:c=d" }
                }
            ]
        };
        var parametersB = new WorkflowGraph
        {
            Nodes =
            [
                new WorkflowNode
                {
                    NodeId = "area",
                    NodeTypeId = "process:geometry.area",
                    Parameters = new Dictionary<string, string> { ["a"] = "b", ["c"] = "d" }
                }
            ]
        };
        Assert.NotEqual(WorkflowPackageGraphHasher.Compute(parametersA), WorkflowPackageGraphHasher.Compute(parametersB));

        WorkflowNode Node(string nodeId) => new()
        {
            NodeId = nodeId,
            NodeTypeId = "process:geometry.area"
        };
        var portsA = new WorkflowGraph
        {
            Nodes = [Node("n1"), Node("n2")],
            Edges =
            [
                new WorkflowEdge
                {
                    SourceNodeId = "n1",
                    TargetNodeId = "n2",
                    SourcePort = "c:d",
                    TargetPort = "e"
                }
            ]
        };
        var portsB = new WorkflowGraph
        {
            Nodes = [Node("n1"), Node("n2")],
            Edges =
            [
                new WorkflowEdge
                {
                    SourceNodeId = "n1",
                    TargetNodeId = "n2",
                    SourcePort = "c",
                    TargetPort = "d:e"
                }
            ]
        };
        Assert.NotEqual(WorkflowPackageGraphHasher.Compute(portsA), WorkflowPackageGraphHasher.Compute(portsB));
    }

    [UnitTest]
    public async Task PublicationInsertFailure_WritesNoDefinition()
    {
        var packages = new SaveFailingPackageStore(new InMemoryWorkflowPackageStore());
        var definitions = new FakeWorkflowDefinitionStore();
        var service = CreateService(packages, definitions);
        var version = await PublishableVersionAsync(service, packages);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishVersionAsync(
            version.PackageId,
            version.Version,
            ScheduleRequest("pub-insert-fail", enabled: true, scheduleEnabled: true, cron: "0 0 * * *"),
            Author(),
            CancellationToken.None));

        Assert.Empty(await definitions.ListAsync());
    }

    [UnitTest]
    public async Task CreateVersion_RejectsAGraphThatDoesNotMatchTheLockedDraft()
    {
        var store = new InMemoryWorkflowPackageStore();
        var package = await store.SavePackageAsync(SamplePackage("pkg-memory"));
        var validated = package.Graph;
        var hash = WorkflowPackageGraphHasher.Compute(validated);
        var drifted = validated with
        {
            Nodes = [validated.Nodes[0] with { Parameters = new Dictionary<string, string> { ["wkb"] = "changed" } }]
        };
        await store.SavePackageAsync(package with { Graph = drifted });

        await Assert.ThrowsAsync<WorkflowPackageGraphConflictException>(() => store.CreateVersionAsync(
            package.PackageId,
            hash,
            validated,
            WorkflowPackageValidationResult.Success(hash),
            "author"));
        Assert.Empty(await store.ListVersionsAsync(package.PackageId));

        var currentHash = WorkflowPackageGraphHasher.Compute(drifted);
        var mismatched = validated;
        await Assert.ThrowsAsync<WorkflowPackageGraphConflictException>(() => store.CreateVersionAsync(
            package.PackageId,
            currentHash,
            mismatched,
            WorkflowPackageValidationResult.Success(currentHash),
            "author"));
    }

    [UnitTest]
    public async Task CreateVersion_StoresTheValidatedGraphWhenOnlyTheScheduleDrifted()
    {
        var store = new InMemoryWorkflowPackageStore();
        var package = await store.SavePackageAsync(SamplePackage("pkg-memory-snapshot"));
        var validated = package.Graph;
        var hash = WorkflowPackageGraphHasher.Compute(validated);
        await store.SavePackageAsync(package with
        {
            Graph = validated with
            {
                Schedule = new WorkflowSchedule { CronExpression = "15 1 * * *", TimeZone = "UTC", Enabled = false }
            }
        });

        var version = await store.CreateVersionAsync(
            package.PackageId,
            hash,
            validated,
            WorkflowPackageValidationResult.Success(hash),
            "author");

        Assert.Null(version.Graph.Schedule);
        Assert.Equal("0101", version.Graph.Nodes[0].Parameters["wkb"]);
    }

    private static async Task<WorkflowPackageVersion> PublishableVersionAsync(Harness harness)
        => await PublishableVersionAsync(harness.Service, harness.Packages);

    private static async Task<WorkflowPackageVersion> PublishableVersionAsync(
        WorkflowPackageService service,
        IWorkflowPackageStore store)
    {
        var saved = await service.SavePackageAsync(
            new SaveWorkflowPackageRequest { Name = "Echo", Graph = EchoGraph() },
            Author(),
            CancellationToken.None);
        var version = await service.CreateVersionAsync(saved.PackageId, Author(), CancellationToken.None);
        Assert.NotNull(await store.GetVersionAsync(saved.PackageId, version.Version));
        return version;
    }

    private static Harness CreateHarness()
    {
        var packages = new InMemoryWorkflowPackageStore();
        var definitions = new FakeWorkflowDefinitionStore();
        return new Harness(CreateService(packages, definitions), packages, definitions);
    }

    private static WorkflowPackageService CreateService(
        IWorkflowPackageStore packages,
        IWorkflowDefinitionStore definitions)
        => new(
            packages,
            new EchoNodeRegistry(),
            new AllowingJobService(),
            TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance,
            definitions);

    private static PublishWorkflowPackageRequest ScheduleRequest(
        string publicationId,
        bool enabled,
        bool scheduleEnabled,
        string cron)
        => new()
        {
            PublicationId = publicationId,
            Target = WorkflowPublicationTarget.Schedule,
            Enabled = enabled,
            Schedule = new WorkflowSchedule
            {
                CronExpression = cron,
                TimeZone = "UTC",
                Enabled = scheduleEnabled
            }
        };

    private static WorkflowGraph EchoGraph()
        => new()
        {
            Nodes = [new WorkflowNode { NodeId = "echo", NodeTypeId = "process:test.echo" }]
        };

    private static WorkflowPackage SamplePackage(string packageId)
        => new()
        {
            PackageId = packageId,
            Name = packageId,
            Graph = new WorkflowGraph
            {
                Nodes =
                [
                    new WorkflowNode
                    {
                        NodeId = "area",
                        NodeTypeId = "process:geometry.area",
                        Parameters = new Dictionary<string, string> { ["wkb"] = "0101" }
                    }
                ]
            },
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };

    private static ClaimsPrincipal Author()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "author")], "test"));

    private sealed record Harness(
        WorkflowPackageService Service,
        InMemoryWorkflowPackageStore Packages,
        FakeWorkflowDefinitionStore Definitions);

    private sealed class EchoNodeRegistry : IWorkflowNodeRegistry
    {
        private static readonly WorkflowNodeDefinition Node = new()
        {
            NodeTypeId = "process:test.echo",
            ProviderId = "test",
            RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
            Title = "Echo",
            Description = "Echo",
            Category = "test",
            ParameterSchemas = [],
            CapabilityFlags = new WorkflowNodeCapabilityFlags
            {
                Executable = true,
                SupportsSchedule = true,
                SupportsJob = true,
                SupportsProcessEndpoint = true
            },
            RuntimeHints = new WorkflowNodeRuntimeHints()
        };

        public Task<WorkflowNodeRegistrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkflowNodeRegistrySnapshot
            {
                RegistryVersion = "test",
                GeneratedAt = DateTimeOffset.UnixEpoch,
                Providers = [],
                Nodes = [Node]
            });

        public Task<WorkflowNodeDefinition?> GetNodeAsync(string nodeTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowNodeDefinition?>(
                string.Equals(nodeTypeId, Node.NodeTypeId, StringComparison.Ordinal) ? Node : null);
    }

    private sealed class AllowingJobService : IGeoprocessingJobService
    {
        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
            AnalysisPlan plan,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(plan);

        public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => new() { IsExecutable = true };

        public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> SubmitJobAsync(
            AnalysisPlan plan,
            string? idempotencyKey,
            ClaimsPrincipal principal,
            IReadOnlyDictionary<string, string>? protocolMetadata = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FailingSetDefinitionStore(FakeWorkflowDefinitionStore inner) : IWorkflowDefinitionStore
    {
        public int DeleteCalls { get; private set; }

        public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
            => inner.GetAsync(workflowId, cancellationToken);

        public Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
            => inner.TryCreateAsync(definition, cancellationToken);

        public Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("definition store unavailable");

        public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => inner.ListAsync(cancellationToken);

        public Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
            => inner.ListScheduledAsync(cancellationToken);

        public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return inner.DeleteAsync(workflowId, cancellationToken);
        }

        public Task<bool> TryClaimScheduleFireAsync(
            string workflowId,
            DateTimeOffset fireTime,
            TimeSpan retention,
            CancellationToken cancellationToken = default)
            => inner.TryClaimScheduleFireAsync(workflowId, fireTime, retention, cancellationToken);

        public Task ReleaseScheduleClaimAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.ReleaseScheduleClaimAsync(workflowId, fireTime, cancellationToken);

        public Task<DateTimeOffset?> GetScheduleCursorAsync(string workflowId, CancellationToken cancellationToken = default)
            => inner.GetScheduleCursorAsync(workflowId, cancellationToken);

        public Task AdvanceScheduleCursorAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.AdvanceScheduleCursorAsync(workflowId, fireTime, cancellationToken);

        public Task SetPendingScheduleCursorAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.SetPendingScheduleCursorAsync(workflowId, fireTime, cancellationToken);

        public Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default)
            => inner.ListEventTriggeredAsync(cancellationToken);

        public Task<string?> GetTriggerCursorAsync(
            string workflowId,
            string cursorKind,
            CancellationToken cancellationToken = default)
            => inner.GetTriggerCursorAsync(workflowId, cursorKind, cancellationToken);

        public Task SetTriggerCursorAsync(
            string workflowId,
            string cursorKind,
            string marker,
            CancellationToken cancellationToken = default)
            => inner.SetTriggerCursorAsync(workflowId, cursorKind, marker, cancellationToken);

        public Task<bool> TryClaimTriggerFireAsync(
            string workflowId,
            string cursorKind,
            string marker,
            TimeSpan retention,
            CancellationToken cancellationToken = default)
            => inner.TryClaimTriggerFireAsync(workflowId, cursorKind, marker, retention, cancellationToken);

        public Task ReleaseTriggerClaimAsync(
            string workflowId,
            string cursorKind,
            string marker,
            CancellationToken cancellationToken = default)
            => inner.ReleaseTriggerClaimAsync(workflowId, cursorKind, marker, cancellationToken);
    }

    private sealed class SaveFailingPackageStore(InMemoryWorkflowPackageStore inner) : IWorkflowPackageStore
    {
        public Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default)
            => inner.ListPackagesAsync(cancellationToken);

        public Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => inner.GetPackageAsync(packageId, cancellationToken);

        public Task<WorkflowPackage> SavePackageAsync(WorkflowPackage package, CancellationToken cancellationToken = default)
            => inner.SavePackageAsync(package, cancellationToken);

        public Task<WorkflowPackageVersion> CreateVersionAsync(
            string packageId,
            string packageHash,
            WorkflowGraph validatedGraph,
            WorkflowPackageValidationResult validation,
            string? createdBy,
            CancellationToken cancellationToken = default)
            => inner.CreateVersionAsync(packageId, packageHash, validatedGraph, validation, createdBy, cancellationToken);

        public Task<WorkflowPackageVersion?> GetVersionAsync(
            string packageId,
            int version,
            CancellationToken cancellationToken = default)
            => inner.GetVersionAsync(packageId, version, cancellationToken);

        public Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
            string packageId,
            CancellationToken cancellationToken = default)
            => inner.ListVersionsAsync(packageId, cancellationToken);

        public Task<WorkflowPublication> SavePublicationAsync(
            WorkflowPublication publication,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publication insert failed");

        public Task<WorkflowPublication?> GetPublicationAsync(
            string publicationId,
            CancellationToken cancellationToken = default)
            => inner.GetPublicationAsync(publicationId, cancellationToken);

        public Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
            string? packageId = null,
            CancellationToken cancellationToken = default)
            => inner.ListPublicationsAsync(packageId, cancellationToken);

        public Task<WorkflowPublication?> DeletePublicationAsync(
            string publicationId,
            CancellationToken cancellationToken = default)
            => inner.DeletePublicationAsync(publicationId, cancellationToken);

        public Task<WorkflowPublication?> SetPublicationStatusAsync(
            string publicationId,
            WorkflowPublicationStatus status,
            CancellationToken cancellationToken = default)
            => inner.SetPublicationStatusAsync(publicationId, status, cancellationToken);

        public Task<bool> TryReplaceWorkflowDefinitionIdAsync(
            string publicationId,
            string expectedDefinitionId,
            string replacementDefinitionId,
            CancellationToken cancellationToken = default)
            => inner.TryReplaceWorkflowDefinitionIdAsync(
                publicationId,
                expectedDefinitionId,
                replacementDefinitionId,
                cancellationToken);
    }

    private sealed class HookedDefinitionStore(
        FakeWorkflowDefinitionStore inner,
        Func<int, WorkflowDefinition, CancellationToken, Task> beforeSet) : IWorkflowDefinitionStore
    {
        private int _sets;

        public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
            => inner.GetAsync(workflowId, cancellationToken);

        public Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
            => inner.TryCreateAsync(definition, cancellationToken);

        public async Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _sets);
            await beforeSet(call, definition, cancellationToken).ConfigureAwait(false);
            await inner.SetAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => inner.ListAsync(cancellationToken);

        public Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
            => inner.ListScheduledAsync(cancellationToken);

        public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(workflowId, cancellationToken);

        public Task<bool> TryClaimScheduleFireAsync(
            string workflowId,
            DateTimeOffset fireTime,
            TimeSpan retention,
            CancellationToken cancellationToken = default)
            => inner.TryClaimScheduleFireAsync(workflowId, fireTime, retention, cancellationToken);

        public Task ReleaseScheduleClaimAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.ReleaseScheduleClaimAsync(workflowId, fireTime, cancellationToken);

        public Task<DateTimeOffset?> GetScheduleCursorAsync(string workflowId, CancellationToken cancellationToken = default)
            => inner.GetScheduleCursorAsync(workflowId, cancellationToken);

        public Task AdvanceScheduleCursorAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.AdvanceScheduleCursorAsync(workflowId, fireTime, cancellationToken);

        public Task SetPendingScheduleCursorAsync(
            string workflowId,
            DateTimeOffset fireTime,
            CancellationToken cancellationToken = default)
            => inner.SetPendingScheduleCursorAsync(workflowId, fireTime, cancellationToken);

        public Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default)
            => inner.ListEventTriggeredAsync(cancellationToken);

        public Task<string?> GetTriggerCursorAsync(
            string workflowId,
            string cursorKind,
            CancellationToken cancellationToken = default)
            => inner.GetTriggerCursorAsync(workflowId, cursorKind, cancellationToken);

        public Task SetTriggerCursorAsync(
            string workflowId,
            string cursorKind,
            string marker,
            CancellationToken cancellationToken = default)
            => inner.SetTriggerCursorAsync(workflowId, cursorKind, marker, cancellationToken);

        public Task<bool> TryClaimTriggerFireAsync(
            string workflowId,
            string cursorKind,
            string marker,
            TimeSpan retention,
            CancellationToken cancellationToken = default)
            => inner.TryClaimTriggerFireAsync(workflowId, cursorKind, marker, retention, cancellationToken);

        public Task ReleaseTriggerClaimAsync(
            string workflowId,
            string cursorKind,
            string marker,
            CancellationToken cancellationToken = default)
            => inner.ReleaseTriggerClaimAsync(workflowId, cursorKind, marker, cancellationToken);
    }
}
