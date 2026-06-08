// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Canonical lifecycle service for Studio package drafts, versions, and publication requests.
/// </summary>
public sealed class StudioPackageLifecycleService : IStudioPackageLifecycleService
{
    /// <summary>Activity source name used by Studio package lifecycle operations.</summary>
    public const string ActivitySourceName = "Honua.Studio.PackageLifecycle";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly IStudioPackageStore _store;
    private readonly IStudioPackageFamilyRegistry _registry;
    private readonly IStudioPackageValidator _validator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new Studio package lifecycle service.
    /// </summary>
    public StudioPackageLifecycleService(
        IStudioPackageStore store,
        IStudioPackageFamilyRegistry registry,
        IStudioPackageValidator validator,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _registry = registry;
        _validator = validator;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public StudioPackageFamilyCapabilities GetCapabilities()
    {
        using var activity = ActivitySource.StartActivity("studio.package.capabilities");
        activity?.SetTag("studio.persistence.mode", _store.PersistenceMode.ToString());
        return _registry.GetCapabilities();
    }

    /// <inheritdoc />
    public async Task<StudioPackageDraft> CreateDraftAsync(
        CreateStudioPackageDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = Start("studio.package.draft.create", command.Envelope.Family, command.ItemId);
        ValidatePackageKey(command.PackageKey);

        var now = _timeProvider.GetUtcNow();
        var validation = _validator.Validate(command.Envelope);
        var itemId = command.ItemId ?? Guid.NewGuid();
        var draft = new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = itemId,
            PackageKey = command.PackageKey.Trim(),
            WorkspaceId = NormalizeOptional(command.WorkspaceId),
            OwnerId = NormalizeOptional(command.OwnerId ?? command.ActorId),
            Family = command.Envelope.Family,
            Envelope = command.Envelope with { Validation = validation },
            Validation = validation,
            BaseVersionId = command.BaseVersionId,
            Generation = 1,
            CreatedBy = command.ActorId,
            UpdatedBy = command.ActorId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = await _store.CreateDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.item.id", created.ItemId.ToString("D"));
        activity?.SetTag("studio.draft.id", created.DraftId.ToString("D"));
        activity?.SetTag("studio.validation.status", created.Validation.Status.ToString());
        return created;
    }

    /// <inheritdoc />
    public async Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.draft.get");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));
        return await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioPackageDraftSummary>> ListDraftsAsync(
        StudioPackageDraftFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var activity = ActivitySource.StartActivity("studio.package.draft.list");
        var normalized = filter.Normalize();
        if (normalized.Family is { } family)
        {
            activity?.SetTag("studio.family", family.ToString());
        }

        return await _store.ListDraftsAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioPackageDraft?> UpdateDraftAsync(
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = Start("studio.package.draft.update", command.Envelope.Family, null);
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));
        ValidatePackageKey(command.PackageKey);

        var existing = await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var requestedGeneration = command.Generation ?? existing.Generation;
        var validation = _validator.Validate(command.Envelope);
        var updated = existing with
        {
            PackageKey = command.PackageKey.Trim(),
            WorkspaceId = NormalizeOptional(command.WorkspaceId),
            OwnerId = NormalizeOptional(command.OwnerId ?? existing.OwnerId),
            Family = command.Envelope.Family,
            Envelope = command.Envelope with { Validation = validation },
            Validation = validation,
            Generation = requestedGeneration,
            UpdatedBy = command.ActorId,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        var stored = await _store.UpdateDraftAsync(updated, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.validation.status", stored?.Validation.Status.ToString());
        return stored;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.draft.delete");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));
        return await _store.DeleteDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioValidationSummary?> ValidateDraftAsync(
        Guid draftId,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.draft.validate");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));

        var draft = await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }

        var validation = _validator.Validate(draft.Envelope);
        var updated = draft with
        {
            Envelope = draft.Envelope with { Validation = validation },
            Validation = validation,
            UpdatedBy = actorId,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        _ = await _store.UpdateDraftAsync(updated, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.validation.status", validation.Status.ToString());
        return validation;
    }

    /// <inheritdoc />
    public async Task<StudioPreviewPlan?> PreviewPlanAsync(
        Guid draftId,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.preview-plan");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));

        var validation = await ValidateDraftAsync(draftId, actorId, cancellationToken).ConfigureAwait(false);
        var draft = await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null || validation is null)
        {
            return null;
        }

        var requiresJob = draft.Family is StudioPackageFamily.Geoprocessing or StudioPackageFamily.Etl or StudioPackageFamily.Workflow;
        var steps = requiresJob
            ? new[] { "validate-envelope", "plan-background-preview-job" }
            : new[] { "validate-envelope", "prepare-inline-preview" };

        return new StudioPreviewPlan
        {
            DraftId = draft.DraftId,
            Family = draft.Family,
            Synchronous = !requiresJob,
            RequiresJob = requiresJob,
            Steps = steps,
            Validation = validation,
        };
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion?> SaveDraftAsVersionAsync(
        Guid draftId,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.version.create");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));

        var draft = await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }

        var validation = _validator.Validate(draft.Envelope);
        var updated = draft with
        {
            Envelope = draft.Envelope with { Validation = validation },
            Validation = validation,
            UpdatedBy = actorId,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        updated = await _store.UpdateDraftAsync(updated, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Studio package draft was not found.");

        var version = await _store.CreateVersionAsync(updated, NormalizeOptional(changeNote), actorId, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.item.id", version.ItemId.ToString("D"));
        activity?.SetTag("studio.version.id", version.VersionId.ToString("D"));
        activity?.SetTag("studio.validation.status", version.Validation.Status.ToString());
        return version;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.version.list");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));
        return await _store.ListVersionsAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion?> GetVersionAsync(
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.version.get");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));
        activity?.SetTag("studio.version.id", versionId.ToString("D"));
        return await _store.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioVersionComparison?> CompareVersionsAsync(
        Guid itemId,
        Guid leftVersionId,
        Guid rightVersionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.version.compare");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));

        var left = await _store.GetVersionAsync(itemId, leftVersionId, cancellationToken).ConfigureAwait(false);
        var right = await _store.GetVersionAsync(itemId, rightVersionId, cancellationToken).ConfigureAwait(false);
        if (left is null || right is null)
        {
            return null;
        }

        var dependenciesEqual = JsonEqualDependencies(left.Dependencies, right.Dependencies);
        var validationEqual = JsonEqualValidation(left.Validation, right.Validation);
        var provenanceEqual = JsonEqualProvenance(left.Provenance, right.Provenance);
        var changes = new List<string>();
        if (!string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal))
        {
            changes.Add("content");
        }
        if (!dependenciesEqual)
        {
            changes.Add("dependencies");
        }
        if (!validationEqual)
        {
            changes.Add("validation");
        }
        if (!provenanceEqual)
        {
            changes.Add("provenance");
        }

        return new StudioVersionComparison
        {
            LeftVersionId = leftVersionId,
            RightVersionId = rightVersionId,
            ContentEqual = !changes.Contains("content", StringComparer.Ordinal),
            DependenciesEqual = dependenciesEqual,
            ValidationEqual = validationEqual,
            ProvenanceEqual = provenanceEqual,
            Changes = changes,
        };
    }

    /// <inheritdoc />
    public async Task<StudioPublicationRequest?> CreatePublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        StudioPublicationIntent? intent,
        string? warningAcknowledgement,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.publish-request.create");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));
        activity?.SetTag("studio.version.id", versionId.ToString("D"));

        var version = await _store.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        if (intent is not null)
        {
            var intentValidation = _validator.ValidatePublicationIntent(intent);
            if (intentValidation.Status == StudioPackageValidationStatus.Invalid)
            {
                throw new ArgumentException(FormatValidationFailure("Publication intent is invalid", intentValidation.Diagnostics));
            }
        }

        var status = version.Validation.Status == StudioPackageValidationStatus.Invalid
            ? StudioPublicationRequestStatus.Rejected
            : StudioPublicationRequestStatus.Accepted;
        var request = new StudioPublicationRequest
        {
            RequestId = Guid.NewGuid(),
            ItemId = itemId,
            VersionId = versionId,
            Intent = intent ?? version.Envelope.PublicationIntent,
            Status = status,
            Validation = version.Validation,
            WarningAcknowledgement = NormalizeOptional(warningAcknowledgement),
            RequestedBy = actorId,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        var stored = await _store.CreatePublicationRequestAsync(request, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.publish.status", stored.Status.ToString());
        return stored;
    }

    /// <inheritdoc />
    public async Task<StudioPackageDraft?> ReopenVersionAsync(
        Guid itemId,
        Guid versionId,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.version.reopen");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));
        activity?.SetTag("studio.version.id", versionId.ToString("D"));

        var version = await _store.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        return await CreateDraftAsync(
            new CreateStudioPackageDraftCommand
            {
                ItemId = itemId,
                PackageKey = version.PackageKey,
                WorkspaceId = version.WorkspaceId,
                OwnerId = version.OwnerId,
                Envelope = version.Envelope,
                ActorId = actorId,
                BaseVersionId = versionId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioRollbackRequest?> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("studio.package.rollback");
        activity?.SetTag("studio.item.id", itemId.ToString("D"));
        activity?.SetTag("studio.version.id", targetVersionId.ToString("D"));

        if (!StudioPackageEnumHelpers.IsDefined(target))
        {
            throw new ArgumentException("Rollback pointer is not supported.", nameof(target));
        }

        var version = await _store.GetVersionAsync(itemId, targetVersionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        return await _store.RollbackAsync(
            itemId,
            targetVersionId,
            target,
            actorId,
            NormalizeOptional(reason),
            cancellationToken).ConfigureAwait(false);
    }

    private static Activity? Start(string name, StudioPackageFamily family, Guid? itemId)
    {
        var activity = ActivitySource.StartActivity(name);
        activity?.SetTag("studio.family", family.ToString());
        if (itemId.HasValue)
        {
            activity?.SetTag("studio.item.id", itemId.Value.ToString("D"));
        }

        return activity;
    }

    private static void ValidatePackageKey(string packageKey)
    {
        if (string.IsNullOrWhiteSpace(packageKey))
        {
            throw new ArgumentException("packageKey is required.", nameof(packageKey));
        }

        var trimmed = packageKey.Trim();
        if (trimmed.Length > 200)
        {
            throw new ArgumentException("packageKey must be 200 characters or fewer.", nameof(packageKey));
        }

        foreach (var c in trimmed)
        {
            var allowed = c is >= 'a' and <= 'z'
                || c is >= 'A' and <= 'Z'
                || c is >= '0' and <= '9'
                || c is '-' or '_' or '.';
            if (!allowed)
            {
                throw new ArgumentException("packageKey may contain only letters, numbers, dash, underscore, or dot.", nameof(packageKey));
            }
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatValidationFailure(
        string prefix,
        IReadOnlyList<StudioValidationDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return prefix + ".";
        }

        return prefix + ": " + string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Message));
    }

    private static bool JsonEqualDependencies(
        IReadOnlyList<StudioPackageDependency> left,
        IReadOnlyList<StudioPackageDependency> right)
        => JsonMultisetEqual(
            left,
            right,
            static dependency => JsonSerializer.Serialize(dependency, StudioJsonContext.Default.StudioPackageDependency));

    private static bool JsonEqualProvenance(
        IReadOnlyList<StudioProvenanceRef> left,
        IReadOnlyList<StudioProvenanceRef> right)
        => JsonMultisetEqual(
            left,
            right,
            static provenance => JsonSerializer.Serialize(provenance, StudioJsonContext.Default.StudioProvenanceRef));

    private static bool JsonEqualValidation(StudioValidationSummary left, StudioValidationSummary right)
        => string.Equals(
            JsonSerializer.Serialize(left with { GeneratedAt = null }, StudioJsonContext.Default.StudioValidationSummary),
            JsonSerializer.Serialize(right with { GeneratedAt = null }, StudioJsonContext.Default.StudioValidationSummary),
            StringComparison.Ordinal);

    private static bool JsonMultisetEqual<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, string> serialize)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftValues = new string[left.Count];
        var rightValues = new string[right.Count];
        for (var i = 0; i < left.Count; i++)
        {
            leftValues[i] = serialize(left[i]);
            rightValues[i] = serialize(right[i]);
        }

        Array.Sort(leftValues, StringComparer.Ordinal);
        Array.Sort(rightValues, StringComparer.Ordinal);
        return leftValues.SequenceEqual(rightValues, StringComparer.Ordinal);
    }
}
