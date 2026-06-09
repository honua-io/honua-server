// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Orchestrates workspace and artifact lifecycle operations.
/// </summary>
internal sealed class WorkspaceLifecycleService : IWorkspaceLifecycleService
{
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IArtifactStore _artifactStore;
    private readonly IRetentionPolicyEvaluator _retentionPolicy;
    private readonly WorkspaceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkspaceLifecycleService> _logger;

    public WorkspaceLifecycleService(
        IWorkspaceStore workspaceStore,
        IArtifactStore artifactStore,
        IRetentionPolicyEvaluator retentionPolicy,
        IOptions<WorkspaceOptions> options,
        TimeProvider timeProvider,
        ILogger<WorkspaceLifecycleService> logger)
    {
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _retentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<Workspace?> GetWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return _workspaceStore.GetAsync(workspaceId, cancellationToken);
    }

    public async Task<Workspace> CreateWorkspaceAsync(
        WorkspaceKind kind,
        string label,
        string ownerId,
        string? scopeId = null,
        TimeSpan? customTtl = null,
        CancellationToken cancellationToken = default)
    {
        if (customTtl.HasValue && customTtl.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(customTtl), customTtl.Value, "Custom TTL must be a positive duration.");
        }

        var now = _timeProvider.GetUtcNow();
        var expiration = customTtl.HasValue
            ? _retentionPolicy.ClampExpiration(kind, now, now + customTtl.Value)
            : _retentionPolicy.ComputeExpiration(kind, now);

        var workspace = new Workspace
        {
            WorkspaceId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Label = label,
            OwnerId = ownerId,
            ScopeId = scopeId,
            State = WorkspaceLifecycleState.Active,
            CreatedAt = now,
            ExpiresAt = expiration
        };

        var created = await _workspaceStore.CreateAsync(workspace, cancellationToken);
        WorkspaceLifecycleLog.WorkspaceCreated(_logger, created.WorkspaceId, kind, expiration);
        return created;
    }

    public async Task<Artifact> AddArtifactAsync(
        string workspaceId,
        ArtifactKind kind,
        string label,
        string? uri = null,
        string? contentType = null,
        long sizeBytes = 0,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        var workspace = await _workspaceStore.GetAsync(workspaceId, cancellationToken);
        if (workspace is null)
        {
            WorkspaceLifecycleLog.AddArtifactRejected(_logger, workspaceId, "not found");
            throw new InvalidOperationException($"Workspace '{workspaceId}' not found");
        }

        if (workspace.State != WorkspaceLifecycleState.Active)
        {
            WorkspaceLifecycleLog.AddArtifactRejected(_logger, workspaceId, workspace.State.ToString());
            throw new InvalidOperationException(
                $"Workspace '{workspaceId}' is in state {workspace.State}; only Active workspaces accept new artifacts");
        }

        var now = _timeProvider.GetUtcNow();
        if (workspace.IsExpired(now))
        {
            WorkspaceLifecycleLog.AddArtifactRejected(_logger, workspaceId, "expired (overdue)");
            throw new InvalidOperationException(
                $"Workspace '{workspaceId}' has passed its expiration time; only unexpired Active workspaces accept new artifacts");
        }

        var artifact = new Artifact
        {
            ArtifactId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Label = label,
            State = ArtifactLifecycleState.Available,
            Uri = uri,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            CreatedAt = now,
            Metadata = metadata ?? new Dictionary<string, string>(),
            WorkspaceId = workspaceId
        };

        var created = await _artifactStore.CreateAsync(artifact, cancellationToken);
        WorkspaceLifecycleLog.ArtifactAdded(_logger, created.ArtifactId, workspaceId, kind);
        return created;
    }

    public async Task<ArtifactPromotionResult> PromoteArtifactAsync(
        ArtifactPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceWorkspace = await _workspaceStore.GetAsync(request.SourceWorkspaceId, cancellationToken);
        if (sourceWorkspace is null)
            return ArtifactPromotionResult.Failure("Source workspace not found");

        var targetWorkspace = await _workspaceStore.GetAsync(request.TargetWorkspaceId, cancellationToken);
        if (targetWorkspace is null)
            return ArtifactPromotionResult.Failure("Target workspace not found");

        if (targetWorkspace.State != WorkspaceLifecycleState.Active)
            return ArtifactPromotionResult.Failure("Target workspace is not active");

        if (targetWorkspace.Kind is not (WorkspaceKind.Persistent or WorkspaceKind.SavedLayer))
            return ArtifactPromotionResult.Failure(
                $"Target workspace kind {targetWorkspace.Kind} is not a durable promotion destination");

        var now = _timeProvider.GetUtcNow();
        if (targetWorkspace.IsExpired(now))
            return ArtifactPromotionResult.Failure("Target workspace has expired");

        var effectiveState = sourceWorkspace.State == WorkspaceLifecycleState.Active && sourceWorkspace.IsExpired(now)
            ? WorkspaceLifecycleState.Expired
            : sourceWorkspace.State;

        if (!_retentionPolicy.IsEligibleForPromotion(sourceWorkspace.Kind, effectiveState))
            return ArtifactPromotionResult.Failure(
                $"Artifacts in {sourceWorkspace.Kind} workspace with state {effectiveState} are not eligible for promotion");

        if (effectiveState == WorkspaceLifecycleState.Expired
            && sourceWorkspace.ExpiresAt.HasValue
            && now >= sourceWorkspace.ExpiresAt.Value + _options.CleanupGracePeriod)
        {
            return ArtifactPromotionResult.Failure(
                "Source workspace has exceeded the cleanup grace period; promotion is no longer allowed");
        }

        var artifact = await _artifactStore.GetAsync(request.ArtifactId, cancellationToken);
        if (artifact is null)
            return ArtifactPromotionResult.Failure("Artifact not found");

        if (artifact.WorkspaceId != request.SourceWorkspaceId)
            return ArtifactPromotionResult.Failure("Artifact does not belong to the specified source workspace");

        if (artifact.State != ArtifactLifecycleState.Available)
            return ArtifactPromotionResult.Failure($"Artifact in state {artifact.State} cannot be promoted");

        var promoted = new Artifact
        {
            ArtifactId = Guid.NewGuid().ToString("N"),
            Kind = artifact.Kind,
            Label = request.NewLabel ?? artifact.Label,
            State = ArtifactLifecycleState.Available,
            Uri = artifact.Uri,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes,
            CreatedAt = now,
            Metadata = artifact.Metadata,
            WorkspaceId = request.TargetWorkspaceId
        };

        var created = await _artifactStore.CreateAsync(promoted, cancellationToken);

        bool transitioned;
        try
        {
            transitioned = await _artifactStore.TransitionStateAsync(
                request.ArtifactId, ArtifactLifecycleState.Promoted, cancellationToken);
        }
        catch (Exception ex)
        {
            WorkspaceLifecycleLog.PromotionTransitionThrew(_logger, request.ArtifactId, ex);
            return await RollbackPromotedCopyAsync(request.ArtifactId, created.ArtifactId,
                "Source artifact state transition threw an exception", cancellationToken);
        }

        if (!transitioned)
        {
            WorkspaceLifecycleLog.PromotionTransitionFailed(_logger, request.ArtifactId);
            return await RollbackPromotedCopyAsync(request.ArtifactId, created.ArtifactId,
                "Failed to mark source artifact as promoted", cancellationToken);
        }

        WorkspaceLifecycleLog.ArtifactPromoted(_logger, request.ArtifactId, created.ArtifactId,
            request.SourceWorkspaceId, request.TargetWorkspaceId);

        return ArtifactPromotionResult.Success(created.ArtifactId);
    }

    private async Task<ArtifactPromotionResult> RollbackPromotedCopyAsync(
        string sourceArtifactId,
        string promotedArtifactId,
        string reason,
        CancellationToken cancellationToken)
    {
        bool rolledBack;
        try
        {
            rolledBack = await _artifactStore.DeleteAsync(promotedArtifactId, cancellationToken);
        }
        catch (Exception ex)
        {
            WorkspaceLifecycleLog.PromotionRollbackDeleteThrew(
                _logger, sourceArtifactId, promotedArtifactId, ex);
            return ArtifactPromotionResult.Failure(
                $"{reason} and rollback of promoted copy also failed; manual cleanup may be required");
        }

        if (!rolledBack)
        {
            WorkspaceLifecycleLog.PromotionRollbackFailed(
                _logger, sourceArtifactId, promotedArtifactId);
            return ArtifactPromotionResult.Failure(
                $"{reason} and rollback of promoted copy also failed; manual cleanup may be required");
        }

        return ArtifactPromotionResult.Failure(reason);
    }

    public async Task<bool> ExtendWorkspaceExpirationAsync(
        string workspaceId,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extension), extension, "Extension must be a positive duration.");
        }

        var workspace = await _workspaceStore.GetAsync(workspaceId, cancellationToken);
        if (workspace is null || workspace.State != WorkspaceLifecycleState.Active)
            return false;

        var now = _timeProvider.GetUtcNow();
        if (workspace.IsExpired(now))
            return false;

        var baseTime = workspace.ExpiresAt ?? now;
        var requested = baseTime + extension;
        var clamped = _retentionPolicy.ClampExpiration(workspace.Kind, workspace.CreatedAt, requested);

        return await _workspaceStore.ExtendExpirationAsync(workspaceId, clamped, cancellationToken);
    }

    public async Task<CleanupResult> RunCleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var expiredWorkspaces = await _workspaceStore.ListExpiredAsync(
            now, _options.MaxCleanupBatchSize, cancellationToken);

        var workspacesExpired = 0;
        var workspacesDeleted = 0;
        var artifactsDeleted = 0;
        long bytesReclaimed = 0;
        var errors = new List<string>();

        foreach (var workspace in expiredWorkspaces)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                // Only Active and Expired workspaces are eligible for cleanup.
                // Archived or Deleted workspaces may have stale ExpiresAt values
                // and must not be processed by the expiration sweep.
                if (workspace.State is not (WorkspaceLifecycleState.Active
                    or WorkspaceLifecycleState.Expired))
                {
                    WorkspaceLifecycleLog.CleanupSkippedNonExpiredState(
                        _logger, workspace.WorkspaceId, workspace.State);
                    continue;
                }

                if (workspace.State == WorkspaceLifecycleState.Active)
                {
                    var expired = await _workspaceStore.TransitionStateAsync(
                        workspace.WorkspaceId, WorkspaceLifecycleState.Expired, cancellationToken);
                    if (expired)
                    {
                        workspacesExpired++;
                        WorkspaceLifecycleLog.WorkspaceExpired(_logger, workspace.WorkspaceId);
                    }
                    else
                    {
                        errors.Add($"Workspace {workspace.WorkspaceId}: failed to transition to Expired");
                        continue;
                    }
                }
                if (workspace.ExpiresAt.HasValue
                    && now >= workspace.ExpiresAt.Value + _options.CleanupGracePeriod)
                {
                    var artifacts = await _artifactStore.ListByWorkspaceAsync(workspace.WorkspaceId, cancellationToken);
                    var allArtifactsDeleted = true;
                    foreach (var artifact in artifacts)
                    {
                        if (artifact.State is not ArtifactLifecycleState.Deleted)
                        {
                            var deleted = await _artifactStore.DeleteAsync(artifact.ArtifactId, cancellationToken);
                            if (deleted)
                            {
                                artifactsDeleted++;
                                bytesReclaimed += artifact.SizeBytes;
                            }
                            else
                            {
                                allArtifactsDeleted = false;
                                errors.Add($"Workspace {workspace.WorkspaceId}: failed to delete artifact {artifact.ArtifactId}");
                            }
                        }
                    }

                    if (!allArtifactsDeleted)
                    {
                        WorkspaceLifecycleLog.CleanupSkippedOrphanRisk(
                            _logger, workspace.WorkspaceId);
                        continue;
                    }

                    var wsDeleted = await _workspaceStore.DeleteAsync(workspace.WorkspaceId, cancellationToken);
                    if (wsDeleted)
                    {
                        workspacesDeleted++;
                        WorkspaceLifecycleLog.WorkspaceDeleted(_logger, workspace.WorkspaceId, artifacts.Count);
                    }
                    else
                    {
                        errors.Add($"Workspace {workspace.WorkspaceId}: failed to delete workspace");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Workspace {workspace.WorkspaceId}: cleanup failed");
                WorkspaceLifecycleLog.CleanupError(_logger, workspace.WorkspaceId, ex);
            }
        }

        return new CleanupResult
        {
            WorkspacesExpired = workspacesExpired,
            WorkspacesDeleted = workspacesDeleted,
            ArtifactsDeleted = artifactsDeleted,
            BytesReclaimed = bytesReclaimed,
            Errors = errors
        };
    }
}

internal static partial class WorkspaceLifecycleLog
{
    [LoggerMessage(
        EventId = 9900,
        Level = LogLevel.Information,
        Message = "Workspace {WorkspaceId} created: kind={Kind}, expiresAt={ExpiresAt}")]
    public static partial void WorkspaceCreated(ILogger logger, string workspaceId, WorkspaceKind kind, DateTimeOffset? expiresAt);

    [LoggerMessage(
        EventId = 9901,
        Level = LogLevel.Debug,
        Message = "Artifact {ArtifactId} added to workspace {WorkspaceId}: kind={Kind}")]
    public static partial void ArtifactAdded(ILogger logger, string artifactId, string workspaceId, ArtifactKind kind);

    [LoggerMessage(
        EventId = 9902,
        Level = LogLevel.Information,
        Message = "Artifact {SourceArtifactId} promoted to {TargetArtifactId}: {SourceWorkspaceId} -> {TargetWorkspaceId}")]
    public static partial void ArtifactPromoted(ILogger logger, string sourceArtifactId, string targetArtifactId, string sourceWorkspaceId, string targetWorkspaceId);

    [LoggerMessage(
        EventId = 9903,
        Level = LogLevel.Information,
        Message = "Workspace {WorkspaceId} expired")]
    public static partial void WorkspaceExpired(ILogger logger, string workspaceId);

    [LoggerMessage(
        EventId = 9904,
        Level = LogLevel.Information,
        Message = "Workspace {WorkspaceId} deleted with {ArtifactCount} artifacts")]
    public static partial void WorkspaceDeleted(ILogger logger, string workspaceId, int artifactCount);

    [LoggerMessage(
        EventId = 9905,
        Level = LogLevel.Error,
        Message = "Error during cleanup of workspace {WorkspaceId}")]
    public static partial void CleanupError(ILogger logger, string workspaceId, Exception exception);

    [LoggerMessage(
        EventId = 9906,
        Level = LogLevel.Warning,
        Message = "Failed to mark source artifact {ArtifactId} as promoted; rolling back promoted copy")]
    public static partial void PromotionTransitionFailed(ILogger logger, string artifactId);

    [LoggerMessage(
        EventId = 9907,
        Level = LogLevel.Error,
        Message = "Failed to roll back promoted copy {PromotedArtifactId} after source {SourceArtifactId} transition failed; duplicate artifact may exist")]
    public static partial void PromotionRollbackFailed(ILogger logger, string sourceArtifactId, string promotedArtifactId);

    [LoggerMessage(
        EventId = 9908,
        Level = LogLevel.Warning,
        Message = "Rejected artifact addition to workspace {WorkspaceId}: {Reason}")]
    public static partial void AddArtifactRejected(ILogger logger, string workspaceId, string reason);

    [LoggerMessage(
        EventId = 9909,
        Level = LogLevel.Error,
        Message = "Source artifact {ArtifactId} state transition threw during promotion")]
    public static partial void PromotionTransitionThrew(ILogger logger, string artifactId, Exception exception);

    [LoggerMessage(
        EventId = 9910,
        Level = LogLevel.Warning,
        Message = "Skipped workspace {WorkspaceId} deletion: not all artifacts were cleaned up")]
    public static partial void CleanupSkippedOrphanRisk(ILogger logger, string workspaceId);

    [LoggerMessage(
        EventId = 9911,
        Level = LogLevel.Error,
        Message = "Rollback delete of promoted copy {PromotedArtifactId} threw after source {SourceArtifactId} transition failed; duplicate artifact may exist")]
    public static partial void PromotionRollbackDeleteThrew(ILogger logger, string sourceArtifactId, string promotedArtifactId, Exception exception);

    [LoggerMessage(
        EventId = 9912,
        Level = LogLevel.Debug,
        Message = "Skipped workspace {WorkspaceId} during cleanup: state {State} is not eligible for expiration")]
    public static partial void CleanupSkippedNonExpiredState(ILogger logger, string workspaceId, WorkspaceLifecycleState state);
}
