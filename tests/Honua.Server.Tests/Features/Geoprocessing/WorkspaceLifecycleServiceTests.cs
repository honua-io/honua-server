// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
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
    public async Task RunCleanup_ExpiresActiveWorkspaces()
    {
        var expired = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddHours(-1));
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
        var gracePeriod = TimeSpan.FromHours(1);
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
    public async Task RunCleanup_ContinuesAfterIndividualError()
    {
        var ws1 = CreateWorkspace("ws-1", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddHours(-1));
        var ws2 = CreateWorkspace("ws-2", WorkspaceKind.Scratch, WorkspaceLifecycleState.Active,
            expiresAt: Now.AddHours(-1));

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
