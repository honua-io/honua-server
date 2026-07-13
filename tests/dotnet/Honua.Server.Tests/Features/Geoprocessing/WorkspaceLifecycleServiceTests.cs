// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Tests for workspace lifecycle service orchestration.
/// </summary>
public class WorkspaceLifecycleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly IWorkspaceStore _workspaceStore = Substitute.For<IWorkspaceStore>();
    private readonly IArtifactStore _artifactStore = Substitute.For<IArtifactStore>();
    private readonly IRetentionPolicyEvaluator _retentionPolicy = Substitute.For<IRetentionPolicyEvaluator>();
    private readonly TimeProvider _timeProvider;
    private readonly WorkspaceLifecycleService _service;

    public WorkspaceLifecycleServiceTests()
    {
        _timeProvider = Substitute.For<TimeProvider>();
        _timeProvider.GetUtcNow().Returns(Now);

        _workspaceStore.CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Workspace>());
        _artifactStore.CreateAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Artifact>());

        _service = new WorkspaceLifecycleService(
            _workspaceStore,
            _artifactStore,
            _retentionPolicy,
            Options.Create(new WorkspaceOptions()),
            _timeProvider,
            NullLogger<WorkspaceLifecycleService>.Instance);
    }

    [Fact]
    public async Task CreateWorkspace_SetsExpirationFromPolicy()
    {
        var expectedExpiration = Now.AddHours(1);
        _retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Now)
            .Returns(expectedExpiration);

        var workspace = await _service.CreateWorkspaceAsync(
            WorkspaceKind.Scratch, "test", "owner-1");

        Assert.Equal(expectedExpiration, workspace.ExpiresAt);
        Assert.Equal(WorkspaceLifecycleState.Active, workspace.State);
        Assert.Equal("owner-1", workspace.OwnerId);
    }

    [Fact]
    public async Task CreateWorkspace_WithCustomTtl_ClampsToPolicy()
    {
        var clamped = Now.AddHours(24);
        _retentionPolicy.ClampExpiration(WorkspaceKind.Scratch, Now, Arg.Any<DateTimeOffset>())
            .Returns(clamped);

        var workspace = await _service.CreateWorkspaceAsync(
            WorkspaceKind.Scratch, "test", "owner-1",
            customTtl: TimeSpan.FromDays(30));

        Assert.Equal(clamped, workspace.ExpiresAt);
    }

    [Fact]
    public async Task CreateWorkspace_PersistentKind_NullExpiration()
    {
        _retentionPolicy.ComputeExpiration(WorkspaceKind.Persistent, Now)
            .Returns((DateTimeOffset?)null);

        var workspace = await _service.CreateWorkspaceAsync(
            WorkspaceKind.Persistent, "durable-ws", "owner-1");

        Assert.Null(workspace.ExpiresAt);
    }

    [Fact]
    public async Task AddArtifact_ActiveWorkspace_SetsAvailableState()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);

        var artifact = await _service.AddArtifactAsync(
            "ws-1", ArtifactKind.FeatureLayer, "result-layer",
            sizeBytes: 2048);

        Assert.Equal(ArtifactLifecycleState.Available, artifact.State);
        Assert.Equal("ws-1", artifact.WorkspaceId);
        Assert.Equal(2048, artifact.SizeBytes);
    }

    [Fact]
    public async Task AddArtifact_MissingWorkspace_Throws()
    {
        _workspaceStore.GetAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns((Workspace?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddArtifactAsync("nonexistent", ArtifactKind.FeatureLayer, "layer"));

        Assert.Contains("not found", ex.Message);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddArtifact_ExpiredWorkspace_Throws()
    {
        SetupWorkspace("ws-expired", WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddArtifactAsync("ws-expired", ArtifactKind.FeatureLayer, "layer"));

        Assert.Contains("only Active workspaces", ex.Message);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddArtifact_DeletedWorkspace_Throws()
    {
        SetupWorkspace("ws-deleted", WorkspaceKind.Scratch, WorkspaceLifecycleState.Deleted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddArtifactAsync("ws-deleted", ArtifactKind.FeatureLayer, "layer"));

        Assert.Contains("only Active workspaces", ex.Message);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOrReplaceArtifact_NoCollision_CreatesArtifact()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Artifact>());

        var artifact = await _service.AddOrReplaceArtifactAsync(
            "ws-1", ArtifactKind.FeatureLayer, "result", overwrite: false, uri: "data:1");

        Assert.Equal("result", artifact.Label);
        Assert.Equal("ws-1", artifact.WorkspaceId);
        await _artifactStore.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOrReplaceArtifact_CollisionWithoutOverwrite_Throws()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        var existing = new Artifact
        {
            ArtifactId = "art-existing",
            Kind = ArtifactKind.FeatureLayer,
            Label = "result",
            State = ArtifactLifecycleState.Available,
            CreatedAt = Now.AddMinutes(-5),
            WorkspaceId = "ws-1"
        };
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([existing]);

        var ex = await Assert.ThrowsAsync<ArtifactAlreadyExistsException>(
            () => _service.AddOrReplaceArtifactAsync(
                "ws-1", ArtifactKind.FeatureLayer, "result", overwrite: false, uri: "data:2"));

        Assert.Contains("result", ex.Message);
        Assert.Contains("overwriteOutput", ex.Message);
        await _artifactStore.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _artifactStore.DidNotReceive().CreateAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOrReplaceArtifact_CollisionWithOverwrite_DeletesExistingAndCreatesNew()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        var existing = new Artifact
        {
            ArtifactId = "art-existing",
            Kind = ArtifactKind.FeatureLayer,
            Label = "result",
            State = ArtifactLifecycleState.Available,
            CreatedAt = Now.AddMinutes(-5),
            WorkspaceId = "ws-1"
        };
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([existing]);
        _artifactStore.DeleteAsync("art-existing", Arg.Any<CancellationToken>()).Returns(true);

        var artifact = await _service.AddOrReplaceArtifactAsync(
            "ws-1", ArtifactKind.FeatureLayer, "result", overwrite: true, uri: "data:2");

        Assert.Equal("result", artifact.Label);
        await _artifactStore.Received(1).DeleteAsync("art-existing", Arg.Any<CancellationToken>());
        await _artifactStore.Received(1).CreateAsync(
            Arg.Is<Artifact>(a => a.Label == "result" && a.Uri == "data:2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOrReplaceArtifact_CollisionWithOverwrite_DeleteFails_AbortsWithoutAdding()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        var existing = new Artifact
        {
            ArtifactId = "art-existing",
            Kind = ArtifactKind.FeatureLayer,
            Label = "result",
            State = ArtifactLifecycleState.Available,
            CreatedAt = Now.AddMinutes(-5),
            WorkspaceId = "ws-1"
        };
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([existing]);
        // The store cannot delete the colliding artifact.
        _artifactStore.DeleteAsync("art-existing", Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<ArtifactReplacementFailedException>(
            () => _service.AddOrReplaceArtifactAsync(
                "ws-1", ArtifactKind.FeatureLayer, "result", overwrite: true, uri: "data:2"));

        Assert.Contains("result", ex.Message);
        Assert.Contains("could not be replaced", ex.Message);
        // The replacement must abort before adding a second Available artifact.
        await _artifactStore.Received(1).DeleteAsync("art-existing", Arg.Any<CancellationToken>());
        await _artifactStore.DidNotReceive().CreateAsync(Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOrReplaceArtifact_IgnoresNonAvailableCollision()
    {
        SetupWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        var deleted = new Artifact
        {
            ArtifactId = "art-old",
            Kind = ArtifactKind.FeatureLayer,
            Label = "result",
            State = ArtifactLifecycleState.Deleted,
            CreatedAt = Now.AddMinutes(-5),
            WorkspaceId = "ws-1"
        };
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([deleted]);

        var artifact = await _service.AddOrReplaceArtifactAsync(
            "ws-1", ArtifactKind.FeatureLayer, "result", overwrite: false, uri: "data:3");

        Assert.Equal("result", artifact.Label);
        await _artifactStore.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateNamedWorkspace_NoExisting_CreatesScratchWorkspace()
    {
        _workspaceStore.ListByOwnerAsync("owner-1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Workspace>());
        _retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Now)
            .Returns(Now.AddHours(1));

        var workspace = await _service.GetOrCreateNamedWorkspaceAsync("owner-1", "my-scratch");

        Assert.Equal(WorkspaceKind.Scratch, workspace.Kind);
        Assert.Equal("my-scratch", workspace.Label);
        Assert.Equal("owner-1", workspace.OwnerId);
        await _workspaceStore.Received(1).CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateNamedWorkspace_ExistingActiveMatch_ReusesWorkspace()
    {
        var existing = CreateWorkspace("ws-existing", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active) with
        {
            Label = "my-scratch",
            ExpiresAt = Now.AddHours(1)
        };
        _workspaceStore.ListByOwnerAsync("owner-1", Arg.Any<CancellationToken>())
            .Returns([existing]);

        var workspace = await _service.GetOrCreateNamedWorkspaceAsync("owner-1", "my-scratch");

        Assert.Equal("ws-existing", workspace.WorkspaceId);
        await _workspaceStore.DidNotReceive().CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateNamedWorkspace_ExistingExpired_CreatesNewWorkspace()
    {
        var expired = CreateWorkspace("ws-expired", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active) with
        {
            Label = "my-scratch",
            ExpiresAt = Now.AddHours(-1)
        };
        _workspaceStore.ListByOwnerAsync("owner-1", Arg.Any<CancellationToken>())
            .Returns([expired]);
        _retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Now)
            .Returns(Now.AddHours(1));

        var workspace = await _service.GetOrCreateNamedWorkspaceAsync("owner-1", "my-scratch");

        Assert.NotEqual("ws-expired", workspace.WorkspaceId);
        await _workspaceStore.Received(1).CreateAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateNamedWorkspace_SameLabelOwnedByDifferentOwner_CreatesCallerOwnedWorkspace()
    {
        // Ownership isolation: another owner's active workspace under the same
        // label must never be resolved for the caller — resolution only consults
        // the caller's own workspaces and lazily creates a caller-owned one.
        var otherOwners = CreateWorkspace("ws-other-owner", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active) with
        {
            Label = "my-scratch",
            OwnerId = "owner-2",
            ExpiresAt = Now.AddHours(1)
        };
        _workspaceStore.ListByOwnerAsync("owner-2", Arg.Any<CancellationToken>())
            .Returns([otherOwners]);
        _workspaceStore.ListByOwnerAsync("owner-1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Workspace>());
        _retentionPolicy.ComputeExpiration(WorkspaceKind.Scratch, Now)
            .Returns(Now.AddHours(1));

        var workspace = await _service.GetOrCreateNamedWorkspaceAsync("owner-1", "my-scratch");

        Assert.NotEqual("ws-other-owner", workspace.WorkspaceId);
        Assert.Equal("owner-1", workspace.OwnerId);
        await _workspaceStore.Received(1).CreateAsync(
            Arg.Is<Workspace>(w => w.OwnerId == "owner-1" && w.Label == "my-scratch"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_SourceNotFound_Fails()
    {
        _workspaceStore.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns((Workspace?)null);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "missing",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Source workspace not found", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_TargetNotActive_Fails()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Expired);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("not active", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_TargetNotDurableKind_Fails()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("not a durable promotion destination", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_NotEligible_Fails()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(false);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("not eligible", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_ValidRequest_CreatesNewArtifactAndMarksSourcePromoted()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target",
            NewLabel = "promoted-layer"
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.PromotedArtifactId);

        await _artifactStore.Received(1).CreateAsync(
            Arg.Is<Artifact>(a =>
                a.WorkspaceId == "target" &&
                a.Label == "promoted-layer" &&
                a.Kind == ArtifactKind.FeatureLayer),
            Arg.Any<CancellationToken>());

        await _artifactStore.Received(1).TransitionStateAsync(
            "art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_TransitionFails_RollsBackAndReturnsFailed()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(false);
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Failed to mark source artifact as promoted", result.FailureReason);
        await _artifactStore.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_TransitionFails_RollbackDeleteFails_ReturnsRollbackFailure()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(false);
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("rollback of promoted copy also failed", result.FailureReason);
        Assert.Contains("manual cleanup", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_TransitionThrows_RollsBackAndReturnsFailed()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("store error"));
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("state transition threw", result.FailureReason);
        await _artifactStore.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_TransitionThrows_RollbackFails_ReturnsManualCleanup()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("store error"));
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("rollback of promoted copy also failed", result.FailureReason);
        Assert.Contains("manual cleanup", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_TransitionFails_RollbackDeleteThrows_ReturnsManualCleanup()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(false);
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("rollback store error"));

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("rollback of promoted copy also failed", result.FailureReason);
        Assert.Contains("manual cleanup", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_TransitionThrows_RollbackDeleteThrows_ReturnsManualCleanup()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "source-layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddMinutes(-30),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>())
            .Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("store error"));
        _artifactStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("rollback store error"));

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("rollback of promoted copy also failed", result.FailureReason);
        Assert.Contains("manual cleanup", result.FailureReason);
    }

    [Fact]
    public async Task PromoteArtifact_PendingArtifact_Fails()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var pendingArtifact = new Artifact
        {
            ArtifactId = "art-pending",
            Kind = ArtifactKind.FeatureLayer,
            Label = "in-progress-layer",
            State = ArtifactLifecycleState.Pending,
            SizeBytes = 512,
            CreatedAt = Now.AddMinutes(-10),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-pending", Arg.Any<CancellationToken>()).Returns(pendingArtifact);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-pending",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Pending", result.FailureReason);
        Assert.Contains("cannot be promoted", result.FailureReason);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Is<Artifact>(a => a.WorkspaceId == "target"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_ExpiredArtifact_Fails()
    {
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var expiredArtifact = new Artifact
        {
            ArtifactId = "art-expired",
            Kind = ArtifactKind.File,
            Label = "stale-file",
            State = ArtifactLifecycleState.Expired,
            SizeBytes = 256,
            CreatedAt = Now.AddHours(-3),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-expired", Arg.Any<CancellationToken>()).Returns(expiredArtifact);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-expired",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Expired", result.FailureReason);
        Assert.Contains("cannot be promoted", result.FailureReason);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Is<Artifact>(a => a.WorkspaceId == "target"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_ExpiresActiveWorkspaces()
    {
        // Expired 30 min ago — within 1h grace period, so only transition occurs.
        var expired = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddMinutes(-30));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([expired]);
        _workspaceStore.TransitionStateAsync("ws-1", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(1, result.WorkspacesExpired);
        Assert.Equal(0, result.WorkspacesDeleted);
        await _workspaceStore.Received(1).TransitionStateAsync(
            "ws-1", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_OverdueActivePastGracePeriod_ExpiresAndDeletesInSameSweep()
    {
        // Active workspace whose ExpiresAt is already past grace period (service was down).
        // The sweep should transition it to Expired AND delete it in the same iteration.
        var overdue = CreateWorkspace("ws-overdue", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddHours(-2));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([overdue]);
        _workspaceStore.TransitionStateAsync("ws-overdue", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>())
            .Returns(true);
        _artifactStore.ListByWorkspaceAsync("ws-overdue", Arg.Any<CancellationToken>())
            .Returns([
                new Artifact
                {
                    ArtifactId = "art-overdue",
                    Kind = ArtifactKind.File,
                    Label = "temp",
                    State = ArtifactLifecycleState.Available,
                    SizeBytes = 1024,
                    CreatedAt = Now.AddHours(-3),
                    WorkspaceId = "ws-overdue"
                }
            ]);
        _artifactStore.DeleteAsync("art-overdue", Arg.Any<CancellationToken>()).Returns(true);
        _workspaceStore.DeleteAsync("ws-overdue", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(1, result.WorkspacesExpired);
        Assert.Equal(1, result.WorkspacesDeleted);
        Assert.Equal(1, result.ArtifactsDeleted);
        Assert.Equal(1024, result.BytesReclaimed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task RunCleanup_TransitionFails_RecordsError()
    {
        var expired = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddHours(-1));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([expired]);
        _workspaceStore.TransitionStateAsync("ws-1", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(0, result.WorkspacesExpired);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task RunCleanup_ArtifactDeleteFails_SkipsWorkspaceDeletion()
    {
        var expiredLongAgo = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired,
            expiresAt: Now.AddHours(-2));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([expiredLongAgo]);
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([
                new Artifact
                {
                    ArtifactId = "art-1",
                    Kind = ArtifactKind.File,
                    Label = "temp",
                    State = ArtifactLifecycleState.Available,
                    SizeBytes = 512,
                    CreatedAt = Now.AddHours(-3),
                    WorkspaceId = "ws-1"
                }
            ]);
        _artifactStore.DeleteAsync("art-1", Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(0, result.ArtifactsDeleted);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Equal(0, result.WorkspacesDeleted);
        Assert.Single(result.Errors);
        await _workspaceStore.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_DeletesExpiredWorkspacesAfterGracePeriod()
    {
        // Default WorkspaceOptions.CleanupGracePeriod is 1 hour; this workspace expired 2 hours
        // ago, i.e. past the grace period, so it must be deleted.
        var expiredLongAgo = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired,
            expiresAt: Now.AddHours(-2));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([expiredLongAgo]);
        _artifactStore.ListByWorkspaceAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns([
                new Artifact
                {
                    ArtifactId = "art-1",
                    Kind = ArtifactKind.File,
                    Label = "temp",
                    State = ArtifactLifecycleState.Available,
                    SizeBytes = 512,
                    CreatedAt = Now.AddHours(-3),
                    WorkspaceId = "ws-1"
                }
            ]);
        _artifactStore.DeleteAsync("art-1", Arg.Any<CancellationToken>()).Returns(true);
        _workspaceStore.DeleteAsync("ws-1", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(0, result.WorkspacesExpired);
        Assert.Equal(1, result.WorkspacesDeleted);
        Assert.Equal(1, result.ArtifactsDeleted);
        Assert.Equal(512, result.BytesReclaimed);
    }

    [Fact]
    public async Task RunCleanup_DoesNotDeleteWithinGracePeriod()
    {
        var recentlyExpired = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired,
            expiresAt: Now.AddMinutes(-30));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([recentlyExpired]);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(0, result.WorkspacesDeleted);
        await _workspaceStore.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_SkipsArchivedWorkspace()
    {
        // Archived workspace with a stale ExpiresAt should not be transitioned or deleted.
        var archived = CreateWorkspace("ws-archived", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Archived, expiresAt: Now.AddHours(-2));
        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([archived]);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(0, result.WorkspacesExpired);
        Assert.Equal(0, result.WorkspacesDeleted);
        Assert.Empty(result.Errors);
        await _workspaceStore.DidNotReceive().TransitionStateAsync(
            Arg.Any<string>(), Arg.Any<WorkspaceLifecycleState>(), Arg.Any<CancellationToken>());
        await _workspaceStore.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_ContinuesAfterIndividualError()
    {
        // Within grace period so only transition is attempted.
        var ws1 = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddMinutes(-30));
        var ws2 = CreateWorkspace("ws-2", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddMinutes(-30));

        _workspaceStore.ListExpiredAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([ws1, ws2]);
        _workspaceStore.TransitionStateAsync("ws-1", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("store error"));
        _workspaceStore.TransitionStateAsync("ws-2", WorkspaceLifecycleState.Expired, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.RunCleanupAsync();

        Assert.Equal(1, result.WorkspacesExpired);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task ExtendExpiration_ActiveWorkspace_Succeeds()
    {
        var workspace = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddMinutes(30));
        _workspaceStore.GetAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns(workspace);
        _retentionPolicy.ClampExpiration(WorkspaceKind.Scratch, workspace.CreatedAt, Arg.Any<DateTimeOffset>())
            .Returns(ci => ci.ArgAt<DateTimeOffset>(2));
        _workspaceStore.ExtendExpirationAsync("ws-1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var success = await _service.ExtendWorkspaceExpirationAsync("ws-1", TimeSpan.FromHours(1));

        Assert.True(success);
    }

    [Fact]
    public async Task ExtendExpiration_NonActiveWorkspace_ReturnsFalse()
    {
        var workspace = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired,
            expiresAt: Now.AddHours(-1));
        _workspaceStore.GetAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns(workspace);

        var success = await _service.ExtendWorkspaceExpirationAsync("ws-1", TimeSpan.FromHours(1));

        Assert.False(success);
    }

    [Fact]
    public async Task ExtendExpiration_OverdueActiveWorkspace_ReturnsFalse()
    {
        var workspace = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddMinutes(-10));
        _workspaceStore.GetAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns(workspace);

        var success = await _service.ExtendWorkspaceExpirationAsync("ws-1", TimeSpan.FromHours(1));

        Assert.False(success);
        await _workspaceStore.DidNotReceive().ExtendExpirationAsync(
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task CreateWorkspace_ZeroOrNegativeCustomTtl_Throws(int minutes)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.CreateWorkspaceAsync(
                WorkspaceKind.Scratch, "test", "owner-1",
                customTtl: TimeSpan.FromMinutes(minutes)));

        Assert.Equal("customTtl", ex.ParamName);
        await _workspaceStore.DidNotReceive().CreateAsync(
            Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task ExtendExpiration_ZeroOrNegativeExtension_Throws(int minutes)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.ExtendWorkspaceExpirationAsync(
                "ws-1", TimeSpan.FromMinutes(minutes)));

        Assert.Equal("extension", ex.ParamName);
        await _workspaceStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_ExpiredWithinGracePeriod_Succeeds()
    {
        var source = CreateWorkspace("source", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Expired, expiresAt: Now.AddMinutes(-30));
        _workspaceStore.GetAsync("source", Arg.Any<CancellationToken>()).Returns(source);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddHours(-1),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>()).Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task PromoteArtifact_ExpiredPastGracePeriod_Fails()
    {
        // Grace period is 1 hour (default). Expired 2 hours ago → past grace.
        var source = CreateWorkspace("source", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Expired, expiresAt: Now.AddHours(-2));
        _workspaceStore.GetAsync("source", Arg.Any<CancellationToken>()).Returns(source);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired)
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("grace period", result.FailureReason);
        await _artifactStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddArtifact_NegativeSizeBytes_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.AddArtifactAsync("ws-1", ArtifactKind.FeatureLayer, "layer", sizeBytes: -1));

        await _workspaceStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddArtifact_OverdueButStillActive_Throws()
    {
        var overdue = CreateWorkspace("ws-overdue", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Active, expiresAt: Now.AddMinutes(-10));
        _workspaceStore.GetAsync("ws-overdue", Arg.Any<CancellationToken>()).Returns(overdue);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AddArtifactAsync("ws-overdue", ArtifactKind.FeatureLayer, "layer"));

        Assert.Contains("passed its expiration time", ex.Message);
        await _artifactStore.DidNotReceive().CreateAsync(
            Arg.Any<Artifact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_OverdueActiveWithinGracePeriod_Succeeds()
    {
        // Workspace is Active in the store but past ExpiresAt. Grace period is 1h (default).
        // Expired 30 min ago → within grace window, effective state is Expired.
        var source = CreateWorkspace("source", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Active, expiresAt: Now.AddMinutes(-30));
        _workspaceStore.GetAsync("source", Arg.Any<CancellationToken>()).Returns(source);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired)
            .Returns(true);

        var sourceArtifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "layer",
            State = ArtifactLifecycleState.Available,
            SizeBytes = 1024,
            CreatedAt = Now.AddHours(-1),
            WorkspaceId = "source"
        };
        _artifactStore.GetAsync("art-1", Arg.Any<CancellationToken>()).Returns(sourceArtifact);
        _artifactStore.TransitionStateAsync("art-1", ArtifactLifecycleState.Promoted, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task PromoteArtifact_OverdueActivePastGracePeriod_Fails()
    {
        // Workspace is Active in the store but past ExpiresAt and past grace period.
        // Expired 2 hours ago, grace period is 1h (default) → beyond grace window.
        var source = CreateWorkspace("source", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Active, expiresAt: Now.AddHours(-2));
        _workspaceStore.GetAsync("source", Arg.Any<CancellationToken>()).Returns(source);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired)
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("grace period", result.FailureReason);
        await _artifactStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_TargetExpiredByClockButActive_Fails()
    {
        // Target workspace is Active in the store but past its ExpiresAt.
        // Promotion should be rejected, consistent with AddArtifactAsync.
        SetupWorkspace("source", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active);
        var target = CreateWorkspace("target", WorkspaceKind.Persistent,
            WorkspaceLifecycleState.Active, expiresAt: Now.AddMinutes(-10));
        _workspaceStore.GetAsync("target", Arg.Any<CancellationToken>()).Returns(target);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active)
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Target workspace has expired", result.FailureReason);
        await _artifactStore.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_ExpiredAtExactGraceBoundary_Fails()
    {
        // Grace period is 1 hour (default). Expired exactly 1 hour ago → at boundary.
        // With >= check, promotion should be rejected at the boundary.
        var source = CreateWorkspace("source", WorkspaceKind.Scratch,
            WorkspaceLifecycleState.Expired, expiresAt: Now.AddHours(-1));
        _workspaceStore.GetAsync("source", Arg.Any<CancellationToken>()).Returns(source);
        SetupWorkspace("target", WorkspaceKind.Persistent, WorkspaceLifecycleState.Active);
        _retentionPolicy.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired)
            .Returns(true);

        var result = await _service.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            ArtifactId = "art-1",
            SourceWorkspaceId = "source",
            TargetWorkspaceId = "target"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("grace period", result.FailureReason);
    }

    private void SetupWorkspace(string id, WorkspaceKind kind, WorkspaceLifecycleState state)
    {
        _workspaceStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(CreateWorkspace(id, kind, state));
    }

    private static Workspace CreateWorkspace(
        string id,
        WorkspaceKind kind,
        WorkspaceLifecycleState state,
        DateTimeOffset? expiresAt = null) => new()
        {
            WorkspaceId = id,
            Kind = kind,
            Label = $"ws-{id}",
            OwnerId = "owner-1",
            State = state,
            CreatedAt = Now.AddHours(-2),
            ExpiresAt = expiresAt
        };
}
