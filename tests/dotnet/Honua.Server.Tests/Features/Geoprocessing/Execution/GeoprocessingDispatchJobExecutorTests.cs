// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the dispatcher that routes claimed geoprocessing jobs
/// to the correct per-process executor. The dispatcher is the single
/// IJobExecutor registered for ExecutionJobKind.Geoprocessing after slice 2;
/// slice 3 extended it with geometry.area + geometry.union; slice 4 added
/// geometry.centroid + geometry.length + geometry.convex-hull; slice 5 adds
/// geometry.dissolve + geometry.simplify + geometry.snap. This test pins
/// the routing contract so unknown process ids never reach a per-process
/// executor by accident.
/// </summary>
public sealed class GeoprocessingDispatchJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_UnknownProcessId_FailsWithSupportedSet()
    {
        var dispatcher = CreateDispatcher();
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-unknown");

        // analytics.cluster is the bare-id catalog process that is NOT job-routed (it
        // executes through the layer-scoped PostGIS analytics protocol path, not the
        // dispatcher) — pick it for the unknown-id smoke. The managed counterpart
        // analytics.cluster-managed IS job-routed (#1260) and so cannot be used here.
        var record = CreateJobRecord("analytics.cluster");

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("analytics.cluster");
        result.ErrorMessage.Should().Contain("geometry.buffer");
        result.ErrorMessage.Should().Contain("geometry.clip");
        result.ErrorMessage.Should().Contain("geometry.intersect");
        result.ErrorMessage.Should().Contain("geometry.project");
        result.ErrorMessage.Should().Contain("geometry.area");
        result.ErrorMessage.Should().Contain("geometry.union");
        result.ErrorMessage.Should().Contain("geometry.centroid");
        result.ErrorMessage.Should().Contain("geometry.length");
        result.ErrorMessage.Should().Contain("geometry.convex-hull");
        result.ErrorMessage.Should().Contain("geometry.dissolve");
        result.ErrorMessage.Should().Contain("geometry.simplify");
        result.ErrorMessage.Should().Contain("geometry.snap");
        result.ErrorMessage.Should().Contain("geometry.make-valid");
        result.ErrorMessage.Should().Contain("geometry.difference");
        result.ErrorMessage.Should().Contain("analytics.spatial-join-managed");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingProcessId_FailsCleanly()
    {
        var dispatcher = CreateDispatcher();
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(processId: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("<none>");
    }

    private static readonly string[] SliceFiveProcessIds =
    {
        "geometry.buffer",
        "geometry.clip",
        "geometry.intersect",
        "geometry.project",
        "geometry.area",
        "geometry.union",
        "geometry.centroid",
        "geometry.length",
        "geometry.convex-hull",
        "geometry.dissolve",
        "geometry.simplify",
        "geometry.snap",
        "geometry.make-valid",
        "geometry.difference",
        "analytics.spatial-join-managed",
        "analytics.cluster-managed",
        "analytics.buffer-aggregate-managed",
        "analytics.density-managed",
        "transform.attribute-rename",
        "transform.attribute-cast",
        "transform.computed-field",
        "transform.attribute-filter",
        "transform.attribute-join",
        "transform.aggregate",
        "transform.pivot",
        "transform.unpivot",
        "transform.spatial-filter",
        "transform.clip",
        "transform.dedup",
        "transform.reproject",
        "source.geojson",
        "source.csv",
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
        "import.dataset",
    };

    [UnitTest]
    public void SupportedProcessIds_ListsSliceFiveExecutors()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.SupportedProcessIds.Should().BeEquivalentTo(SliceFiveProcessIds);
    }

    [UnitTest]
    public void Kind_IsGeoprocessing()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
    }

    // -----------------------------------------------------------------------
    // env:workspace / env:overwriteOutput routing (GPServer submitJob/execute)
    // -----------------------------------------------------------------------

    [UnitTest]
    public async Task ExecuteAsync_NoWorkspaceRequested_PublishesArtifactWithoutTouchingWorkspaceService()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-no-workspace");

        var record = CreateFakeExecutorJobRecord(workspaceId: null, overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await context.Received(1).PublishArtifactAsync("data:fake-artifact", Arg.Any<CancellationToken>());
        await workspaceLifecycle.DidNotReceiveWithAnyArgs().AddOrReplaceArtifactAsync(
            default!, default, default!, default, cancellationToken: default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequested_RoutesArtifactThroughWorkspaceLifecycle()
    {
        // env:workspace is a caller-supplied LABEL, not a durable workspace id —
        // the dispatcher must resolve it via GetOrCreateNamedWorkspaceAsync before
        // any artifact write; downstream calls use the RESOLVED Workspace.WorkspaceId
        // ("ws-1-resolved" here), never the raw "ws-1" label from the request.
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle
            .GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "ws-1-resolved",
                Kind = WorkspaceKind.Scratch,
                Label = "ws-1",
                OwnerId = "admin",
                State = WorkspaceLifecycleState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-workspace");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await workspaceLifecycle.Received(1).GetOrCreateNamedWorkspaceAsync(
            "admin", "ws-1", Arg.Any<CancellationToken>());
        await workspaceLifecycle.Received(1).PublishArtifactAsync(
                Arg.Is<WorkspaceArtifactPublication>(p => p.WorkspaceId == "ws-1-resolved" && p.Kind == ArtifactKind.File && p.Label == "artifact1" && !p.Overwrite && p.Reference == "data:fake-artifact"),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
        // The raw label must never be used as a workspace id downstream.
        await workspaceLifecycle.DidNotReceive().PublishArtifactAsync(
            Arg.Is<WorkspaceArtifactPublication>(p => p.WorkspaceId == "ws-1"),
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
        // The workspace ledger write does not replace the durable job-record publish.
        await context.Received(1).TryPublishArtifactAsync("data:fake-artifact", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequested_InnerPublishRejected_DoesNotWriteWorkspaceLedger()
    {
        // The durable publish is the gate: if the real JobExecutionService rejects/
        // throws on PublishArtifactAsync (lost lease, cancellation won, job no longer
        // owned), the workspace ledger must NOT record an artifact that was never
        // durably published. Proves the inner publish runs before — and gates — the
        // workspace write.
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle
            .GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "ws-1-resolved",
                Kind = WorkspaceKind.Scratch,
                Label = "ws-1",
                OwnerId = "admin",
                State = WorkspaceLifecycleState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-inner-rejected");
        // The durable publish is rejected/throws (e.g. lease lost / job cancelled).
        context.TryPublishArtifactAsync("data:fake-artifact", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("job no longer owns its lease"));

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var act = async () => await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        // The ledger write must never have run once the inner publish failed.
        await workspaceLifecycle.DidNotReceiveWithAnyArgs().AddOrReplaceArtifactAsync(
            default!, default, default!, default, cancellationToken: default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceCollisionWithoutOverwrite_FailsWithClearMessage()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle
            .GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "ws-1-resolved",
                Kind = WorkspaceKind.Scratch,
                Label = "ws-1",
                OwnerId = "admin",
                State = WorkspaceLifecycleState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
        workspaceLifecycle
            .PublishArtifactAsync(
                Arg.Is<WorkspaceArtifactPublication>(p => p.WorkspaceId == "ws-1-resolved" && p.Kind == ArtifactKind.File && p.Label == "artifact1" && !p.Overwrite && p.Reference == "data:fake-artifact"),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArtifactAlreadyExistsException("ws-1-resolved", "artifact1"));

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-collision");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("artifact1");
        result.ErrorMessage.Should().Contain("overwriteOutput");
        result.IsRetryable.Should().BeFalse();
        // Collisions fail before a reference is appended to the durable job.
        await context.DidNotReceiveWithAnyArgs().TryPublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceCollisionWithOverwrite_SucceedsAndReplaces()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle
            .GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "ws-1-resolved",
                Kind = WorkspaceKind.Scratch,
                Label = "ws-1",
                OwnerId = "admin",
                State = WorkspaceLifecycleState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });

        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-overwrite");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: true);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await workspaceLifecycle.Received(1).PublishArtifactAsync(
                Arg.Is<WorkspaceArtifactPublication>(p => p.WorkspaceId == "ws-1-resolved" && p.Kind == ArtifactKind.File && p.Label == "artifact1" && p.Overwrite && p.Reference == "data:fake-artifact"),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequested_ResolvesLabelThroughRealWorkspaceLifecycleService()
    {
        // Unlike the tests above (which mock IWorkspaceLifecycleService directly and
        // so would happily pass even if GetOrCreateNamedWorkspaceAsync were dead code),
        // this drives the REAL WorkspaceLifecycleService implementation end to end so a
        // missing/unwired GetOrCreateNamedWorkspaceAsync call cannot hide behind a mock.
        var workspaceStore = Substitute.For<IWorkspaceStore>();
        var artifactStore = Substitute.For<IArtifactStore, IAtomicWorkspaceStore>();
        var atomicArtifacts = (IAtomicWorkspaceStore)artifactStore;
        atomicArtifacts.PublishAsync(Arg.Any<Artifact>(), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.Arg<Func<CancellationToken, Task<bool>>>()(call.Arg<CancellationToken>())
                ? call.Arg<Artifact>() : null);
        var retentionPolicy = Substitute.For<IRetentionPolicyEvaluator>();

        Workspace? storedWorkspace = null;
        workspaceStore.ListByOwnerAsync("admin", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Workspace>());
        workspaceStore.CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedWorkspace = ci.Arg<Workspace>();
                return storedWorkspace;
            });
        workspaceStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => storedWorkspace);
        retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Arg.Any<DateTimeOffset>())
            .Returns((DateTimeOffset?)null);

        artifactStore.ListByWorkspaceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Artifact>());
        artifactStore.CreateAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Artifact>());

        var services = new ServiceCollection();
        services.AddSingleton(workspaceStore);
        services.AddSingleton(artifactStore);
        services.AddSingleton(retentionPolicy);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WorkspaceOptions>>(Options.Create(new WorkspaceOptions()));
        services.AddSingleton<ILogger<WorkspaceLifecycleService>>(NullLogger<WorkspaceLifecycleService>.Instance);
        services.AddSingleton<IWorkspaceLifecycleService, WorkspaceLifecycleService>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var dispatcher = CreateFakeExecutorDispatcher(scopeFactory);
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-real-workspace");

        var record = CreateFakeExecutorJobRecord(workspaceId: "my-scratch-label", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // GetOrCreateNamedWorkspaceAsync must have actually run: a workspace was
        // created (no existing one matched the label) with that label, owned by
        // the job's requester.
        await workspaceStore.Received(1).CreateAsync(
            Arg.Is<Workspace>(w => w.Label == "my-scratch-label" && w.Kind == WorkspaceKind.Scratch && w.OwnerId == "admin"),
            Arg.Any<CancellationToken>());
        storedWorkspace.Should().NotBeNull();
        storedWorkspace!.WorkspaceId.Should().NotBe("my-scratch-label",
            "the resolved workspace id must be the durable id GetOrCreateNamedWorkspaceAsync minted, not the raw env:workspace label");

        // The artifact write must use the RESOLVED workspace id, not the raw label.
        await atomicArtifacts.Received(1).PublishAsync(
            Arg.Is<Artifact>(a => a.WorkspaceId == storedWorkspace.WorkspaceId && a.Label == "artifact1"), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceLabelMatchingOtherOwnersWorkspace_NeverTargetsOtherOwnersWorkspace()
    {
        // Cross-owner isolation end to end (real WorkspaceLifecycleService): a
        // caller whose env:workspace label — or even overwriteOutput=true — matches
        // another owner's active workspace must lazily get their OWN workspace.
        // The other owner's workspace is never resolved, and its Available
        // artifact under the same output label is never deleted/replaced.
        var workspaceStore = Substitute.For<IWorkspaceStore>();
        var artifactStore = Substitute.For<IArtifactStore, IAtomicWorkspaceStore>();
        var atomicArtifacts = (IAtomicWorkspaceStore)artifactStore;
        atomicArtifacts.PublishAsync(Arg.Any<Artifact>(), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call => await call.Arg<Func<CancellationToken, Task<bool>>>()(call.Arg<CancellationToken>())
                ? call.Arg<Artifact>() : null);
        var retentionPolicy = Substitute.For<IRetentionPolicyEvaluator>();

        var victimWorkspace = new Workspace
        {
            WorkspaceId = "ws-victim",
            Kind = WorkspaceKind.Scratch,
            Label = "shared-label",
            OwnerId = "victim",
            State = WorkspaceLifecycleState.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var victimArtifact = new Artifact
        {
            ArtifactId = "art-victim",
            Kind = ArtifactKind.File,
            Label = "artifact1",
            State = ArtifactLifecycleState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "ws-victim"
        };

        Workspace? callerWorkspace = null;
        // Owner-scoped listing: the victim's workspace is visible ONLY under the
        // victim's owner id; the calling owner ("admin") owns nothing yet.
        workspaceStore.ListByOwnerAsync("victim", Arg.Any<CancellationToken>())
            .Returns([victimWorkspace]);
        workspaceStore.ListByOwnerAsync("admin", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Workspace>());
        workspaceStore.CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callerWorkspace = ci.Arg<Workspace>();
                return callerWorkspace;
            });
        workspaceStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<string>() == "ws-victim" ? victimWorkspace : callerWorkspace);
        retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Arg.Any<DateTimeOffset>())
            .Returns((DateTimeOffset?)null);

        artifactStore.ListByWorkspaceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<string>() == "ws-victim"
                ? new[] { victimArtifact }
                : Array.Empty<Artifact>());
        artifactStore.CreateAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Artifact>());

        var services = new ServiceCollection();
        services.AddSingleton(workspaceStore);
        services.AddSingleton(artifactStore);
        services.AddSingleton(retentionPolicy);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WorkspaceOptions>>(Options.Create(new WorkspaceOptions()));
        services.AddSingleton<ILogger<WorkspaceLifecycleService>>(NullLogger<WorkspaceLifecycleService>.Instance);
        services.AddSingleton<IWorkspaceLifecycleService, WorkspaceLifecycleService>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var dispatcher = CreateFakeExecutorDispatcher(scopeFactory);
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-cross-owner");

        // overwriteOutput=true is the worst case: if the victim's workspace were
        // (wrongly) resolved, its "artifact1" would be deleted and replaced.
        var record = CreateFakeExecutorJobRecord(workspaceId: "shared-label", overwriteOutput: true);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // A caller-owned workspace was lazily created for the label.
        await workspaceStore.Received(1).CreateAsync(
            Arg.Is<Workspace>(w => w.OwnerId == "admin" && w.Label == "shared-label"),
            Arg.Any<CancellationToken>());
        callerWorkspace.Should().NotBeNull();
        callerWorkspace!.WorkspaceId.Should().NotBe("ws-victim");

        // The victim's workspace/artifact must be completely untouched.
        await artifactStore.DidNotReceive().DeleteAsync("art-victim", Arg.Any<CancellationToken>());
        await atomicArtifacts.DidNotReceive().PublishAsync(
            Arg.Is<Artifact>(a => a.WorkspaceId == "ws-victim"), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
        await atomicArtifacts.Received(1).PublishAsync(
            Arg.Is<Artifact>(a => a.WorkspaceId == callerWorkspace.WorkspaceId && a.Label == "artifact1"), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
            Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceRequestedWithNoProviderConfigured_FailsFastWithoutRunningHandler()
    {
        // No IServiceScopeFactory at all: the dispatcher itself was constructed
        // without one (mirrors positional test construction / hosts with no
        // workspace storage provider registered).
        var dispatcher = CreateFakeExecutorDispatcher(scopeFactory: null);
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        context.OperationId.Returns("op-no-provider");

        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ws-1");
        result.ErrorMessage.Should().Contain("no workspace storage provider");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceProviderNotRegistered_FailsPermanentlyWithoutRunningHandler()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = CreateFakeExecutorDispatcher(services.GetRequiredService<IServiceScopeFactory>());
        var context = Substitute.For<IJobExecutionContext>();
        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no workspace storage provider");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceResolutionThrows_RetainsRetryAndSanitizesFailure()
    {
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle.GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Private provider connection detail"));
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeTrue();
        result.ErrorMessage.Should().Be("env:workspace='ws-1' could not be resolved.");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceQuotaReached_FailsPermanentlyBeforePublishing()
    {
        var lifecycle = Substitute.For<IWorkspaceLifecycleService>();
        lifecycle.GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkspaceQuotaExceededException());
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(lifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);
        var result = await dispatcher.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("workspace count limit");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_WorkspaceResolutionCancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var workspaceLifecycle = Substitute.For<IWorkspaceLifecycleService>();
        ConfigurePublication(workspaceLifecycle);
        workspaceLifecycle.GetOrCreateNamedWorkspaceAsync("admin", "ws-1", Arg.Any<CancellationToken>())
            .Returns<Workspace>(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(workspaceLifecycle));
        var context = Substitute.For<IJobExecutionContext>();
        var record = CreateFakeExecutorJobRecord(workspaceId: "ws-1", overwriteOutput: null);

        var act = () => dispatcher.ExecuteAsync(record, context, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_ArtifactQuotaFailureIsPermanentAndDoesNotPublishReference()
    {
        var lifecycle = Substitute.For<IWorkspaceLifecycleService>();
        lifecycle.GetOrCreateNamedWorkspaceAsync("admin", "limited", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "limited-id",
                OwnerId = "admin",
                Label = "limited",
                Kind = WorkspaceKind.Scratch,
                State = WorkspaceLifecycleState.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
        lifecycle.PublishArtifactAsync(Arg.Any<WorkspaceArtifactPublication>(),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkspaceQuotaExceededException("The workspace artifact count or recorded storage limit has been reached."));
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("quota-operation");
        var dispatcher = CreateFakeExecutorDispatcher(BuildScopeFactory(lifecycle));
        var result = await dispatcher.ExecuteAsync(CreateFakeExecutorJobRecord("limited", false), context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("storage limit");
        await context.DidNotReceiveWithAnyArgs().TryPublishArtifactAsync(default!, default);
    }

    private static void ConfigurePublication(IWorkspaceLifecycleService lifecycle)
    {
        lifecycle.PublishArtifactAsync(Arg.Any<WorkspaceArtifactPublication>(),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (!await call.Arg<Func<CancellationToken, Task<bool>>>()(call.Arg<CancellationToken>()))
                {
                    return null;
                }
                var output = call.Arg<WorkspaceArtifactPublication>();
                return new Artifact
                {
                    ArtifactId = "published",
                    WorkspaceId = output.WorkspaceId,
                    Kind = output.Kind,
                    Label = output.Label,
                    Uri = output.Reference,
                    State = ArtifactLifecycleState.Available,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            });
    }

    private static IServiceScopeFactory BuildScopeFactory(IWorkspaceLifecycleService workspaceLifecycle)
    {
        var services = new ServiceCollection();
        services.AddSingleton(workspaceLifecycle);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Job record routed to <see cref="FakeArtifactPublishingExecutor"/> (registered
    /// only in the dispatchers these workspace-routing tests build), isolating the
    /// workspace-routing behavior from real geometry executor logic.
    /// </summary>
    private static ExecutionJobRecord CreateFakeExecutorJobRecord(string? workspaceId, bool? overwriteOutput)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = FakeArtifactPublishingExecutor.HandledProcessId,
            ["protocolProcessId"] = FakeArtifactPublishingExecutor.HandledProcessId,
            [$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}0"] = "artifact1"
        };

        if (workspaceId is not null)
        {
            parameters[GeoprocessingProtocolMetadataKeys.GPServerWorkspace] = workspaceId;
        }

        if (overwriteOutput is { } overwrite)
        {
            parameters[GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput] = overwrite ? "true" : "false";
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-fake",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { RequestedBy = "admin" },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }

    /// <summary>
    /// Minimal <see cref="IProcessExecutor"/> that publishes a single fixed
    /// artifact and succeeds, used to isolate workspace-routing tests from real
    /// geometry executor logic.
    /// </summary>
    private sealed class FakeArtifactPublishingExecutor : IProcessExecutor
    {
        public const string HandledProcessId = "test.fake-artifact-publisher";

        public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string> { HandledProcessId };

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
        {
            await context.PublishArtifactAsync("data:fake-artifact", cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
    }

    /// <summary>
    /// Builds a dispatcher routing only <see cref="FakeArtifactPublishingExecutor"/>,
    /// isolated from <see cref="CreateDispatcher"/>'s full executor set so the
    /// exact-match <see cref="SupportedProcessIds_ListsSliceFiveExecutors"/>
    /// assertion is unaffected by these workspace-routing tests.
    /// </summary>
    private static GeoprocessingDispatchJobExecutor CreateFakeExecutorDispatcher(
        IServiceScopeFactory? scopeFactory)
    {
        IProcessExecutor[] executors = { new FakeArtifactPublishingExecutor() };
        return new GeoprocessingDispatchJobExecutor(
            executors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance,
            usageTelemetry: null,
            serviceScopeFactory: scopeFactory);
    }

    private static GeoprocessingDispatchJobExecutor CreateDispatcher()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);

        // Auto-registration contract (#2122): the dispatcher routes by enumerating
        // the IProcessExecutor set and keying on each executor's self-declared
        // ProcessIds, so the test composes the same executor instances as a flat
        // list instead of the former positional constructor.
        IProcessExecutor[] executors =
        {
            new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance),
            new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance),
            new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance),
            new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance),
            new GeometryAreaJobExecutor(monitor, NullLogger<GeometryAreaJobExecutor>.Instance),
            new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance),
            new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance),
            new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance),
            new GeometryConvexHullJobExecutor(monitor, NullLogger<GeometryConvexHullJobExecutor>.Instance),
            new GeometryDissolveJobExecutor(monitor, NullLogger<GeometryDissolveJobExecutor>.Instance),
            new GeometrySimplifyJobExecutor(monitor, NullLogger<GeometrySimplifyJobExecutor>.Instance),
            new GeometrySnapJobExecutor(monitor, NullLogger<GeometrySnapJobExecutor>.Instance),
            new GeometryMakeValidJobExecutor(monitor, NullLogger<GeometryMakeValidJobExecutor>.Instance),
            new GeometryDifferenceJobExecutor(monitor, NullLogger<GeometryDifferenceJobExecutor>.Instance),
            new ManagedSpatialJoinExecutor(monitor),
            new ManagedClusterExecutor(monitor),
            new ManagedBufferAggregateExecutor(monitor),
            new ManagedDensityExecutor(monitor),
            new AttributeRenameTransformExecutor(monitor),
            new AttributeCastTransformExecutor(monitor),
            new ComputedFieldTransformExecutor(monitor),
            new AttributeFilterTransformExecutor(monitor),
            new AttributeJoinTransformExecutor(monitor),
            new AggregateTransformExecutor(monitor),
            new PivotTransformExecutor(monitor),
            new UnpivotTransformExecutor(monitor),
            new SpatialFilterTransformExecutor(monitor),
            new ClipTransformExecutor(monitor),
            new DedupTransformExecutor(monitor),
            new ReprojectTransformExecutor(monitor),
            new GeoJsonSourceExecutor(monitor),
            new CsvSourceExecutor(monitor),
            new GeoJsonFileSinkExecutor(monitor),
            new QuarantineSinkExecutor(monitor),
            new ExternalPostgisSinkExecutor(monitor),
            new ImportDatasetJobExecutor(
                Substitute.For<IServiceScopeFactory>(),
                NullLogger<ImportDatasetJobExecutor>.Instance,
                Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>()),
        };

        return new GeoprocessingDispatchJobExecutor(
            executors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);
    }

    private static ExecutionJobRecord CreateJobRecord(string? processId)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(processId))
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId;
            parameters["protocolProcessId"] = processId;
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }
}
