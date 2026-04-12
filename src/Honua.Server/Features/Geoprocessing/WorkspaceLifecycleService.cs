// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geoprocessing;

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

    public async Task<Workspace> CreateWorkspaceAsync(
        WorkspaceKind kind,
        string label,
        string ownerId,
        string? scopeId = null,
        TimeSpan? customTtl = null,
        CancellationToken cancellationToken = default)
    {
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
        var artifact = new Artifact
        {
            ArtifactId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Label = label,
            State = ArtifactLifecycleState.Available,
            Uri = uri,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            CreatedAt = _timeProvider.GetUtcNow(),
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

        if (!_retentionPolicy.IsEligibleForPromotion(sourceWorkspace.Kind, sourceWorkspace.State))
            return ArtifactPromotionResult.Failure(
                $"Artifacts in {sourceWorkspace.Kind} workspace with state {sourceWorkspace.State} are not eligible for promotion");

        var artifact = await _artifactStore.GetAsync(request.ArtifactId, cancellationToken);
        if (artifact is null)
            return ArtifactPromotionResult.Failure("Artifact not found");

        if (artifact.WorkspaceId != request.SourceWorkspaceId)
            return ArtifactPromotionResult.Failure("Artifact does not belong to the specified source workspace");

        if (artifact.State is ArtifactLifecycleState.Deleted or ArtifactLifecycleState.Promoted)
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
            CreatedAt = _timeProvider.GetUtcNow(),
            Metadata = artifact.Metadata,
            WorkspaceId = request.TargetWorkspaceId
        };

        var created = await _artifactStore.CreateAsync(promoted, cancellationToken);
        var transitioned = await _artifactStore.TransitionStateAsync(
            request.ArtifactId, ArtifactLifecycleState.Promoted, cancellationToken);

        if (!transitioned)
        {
            // Roll back the promoted copy so the caller can safely retry.
            var rolledBack = await _artifactStore.DeleteAsync(created.ArtifactId, cancellationToken);
            if (!rolledBack)
            {
                WorkspaceLifecycleLog.PromotionRollbackFailed(
                    _logger, request.ArtifactId, created.ArtifactId);
                return ArtifactPromotionResult.Failure(
                    "Failed to mark source artifact as promoted and rollback of promoted copy also failed; manual cleanup may be required");
            }

            WorkspaceLifecycleLog.PromotionTransitionFailed(_logger, request.ArtifactId);
            return ArtifactPromotionResult.Failure("Failed to mark source artifact as promoted");
        }

        WorkspaceLifecycleLog.ArtifactPromoted(_logger, request.ArtifactId, created.ArtifactId,
            request.SourceWorkspaceId, request.TargetWorkspaceId);

        return ArtifactPromotionResult.Success(created.ArtifactId);
    }

    public async Task<bool> ExtendWorkspaceExpirationAsync(
        string workspaceId,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _workspaceStore.GetAsync(workspaceId, cancellationToken);
        if (workspace is null || workspace.State != WorkspaceLifecycleState.Active)
            return false;

        var baseTime = workspace.ExpiresAt ?? _timeProvider.GetUtcNow();
        var requested = baseTime + extension;
        var clamped = _retentionPolicy.ClampExpiration(workspace.Kind, workspace.CreatedAt, requested);

        return await _workspaceStore.ExtendExpirationAsync(workspaceId, clamped, cancellationToken);
    }

    public async Task<CleanupResult> RunCleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var expiredWorkspaces = await _workspaceStore.ListExpiredAsync(now, cancellationToken);

        var workspacesExpired = 0;
        var workspacesDeleted = 0;
        var artifactsDeleted = 0;
        long bytesReclaimed = 0;
        var errors = new List<string>();

        var batch = expiredWorkspaces.Take(_options.MaxCleanupBatchSize);

        foreach (var workspace in batch)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
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
                    }

                    continue;
                }

                if (workspace.State == WorkspaceLifecycleState.Expired
                    && workspace.ExpiresAt.HasValue
                    && now >= workspace.ExpiresAt.Value + _options.CleanupGracePeriod)
                {
                    var artifacts = await _artifactStore.ListByWorkspaceAsync(workspace.WorkspaceId, cancellationToken);
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
                                errors.Add($"Workspace {workspace.WorkspaceId}: failed to delete artifact {artifact.ArtifactId}");
                            }
                        }
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
                errors.Add($"Workspace {workspace.WorkspaceId}: {ex.Message}");
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
        Message = "Failed to mark source artifact {ArtifactId} as promoted; promoted copy rolled back")]
    public static partial void PromotionTransitionFailed(ILogger logger, string artifactId);

    [LoggerMessage(
        EventId = 9907,
        Level = LogLevel.Error,
        Message = "Failed to roll back promoted copy {PromotedArtifactId} after source {SourceArtifactId} transition failed; duplicate artifact may exist")]
    public static partial void PromotionRollbackFailed(ILogger logger, string sourceArtifactId, string promotedArtifactId);
}
