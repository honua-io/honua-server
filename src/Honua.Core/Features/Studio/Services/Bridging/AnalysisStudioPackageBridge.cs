// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services.Bridging;

/// <summary>
/// Bridges the Studio <c>analysis</c> package family onto the native analysis content store
/// (ADR-0069, honua-server#3004). Envelope bodies are native <see cref="AnalysisPackageContent"/>
/// payloads serialized with the family's own <see cref="AnalysisContentJsonContext"/>, and the
/// native job/artifact provenance links (<c>createdFromJobId</c>, <c>createdFromArtifactIds</c>,
/// <c>basedOnVersionId</c>) are projected into the envelope's <c>dependencies</c> sidecar and
/// written back on save, so lifecycle-authored versions keep the joins the analysis artifacts
/// and jobs APIs resolve. Saves append immutable native versions with the same monotonic
/// numbering and conflict-retry the native service uses.
/// </summary>
public sealed class AnalysisStudioPackageBridge : IStudioFamilyPersistenceBridge
{
    private const string IdNamespace = "analysis";
    private const string VersionIdNamespace = "analysis-version";
    private const string NativeIdPrefix = "analysis-content-";
    private const int MaxVersionCreateAttempts = 3;

    /// <summary>Native package format for bridged analysis content.</summary>
    public const string NativeFormat = "honua.analysis-content.v1";

    /// <summary>Dependency kind used for job provenance links.</summary>
    public const string JobDependencyKind = "job";

    /// <summary>Dependency kind used for artifact provenance links.</summary>
    public const string ArtifactDependencyKind = "artifact";

    private static readonly StudioPackageOperation[] Operations =
    [
        StudioPackageOperation.DraftCreate,
        StudioPackageOperation.DraftRead,
        StudioPackageOperation.DraftUpdate,
        StudioPackageOperation.Validate,
        StudioPackageOperation.PreviewPlan,
        StudioPackageOperation.ContentVersionCreate,
        StudioPackageOperation.ContentVersionRead,
        StudioPackageOperation.ContentVersionCompare,
        StudioPackageOperation.Reopen,
    ];

    private static readonly string[] BridgeLimitations =
    [
        "analysis packages persist through the native analysis content store (/api/v1/analysis/content); versions are append-only with server-assigned numbering",
        "bridged analysis items appear in content-item enumeration once first saved; unsaved drafts are listed under /studio/package-drafts",
        "content-item enumeration merges at most 1000 bridged analysis items",
        "publish-request and rollback are not supported for the bridged analysis family; route publication is handled by the Content Publication Registry",
    ];

    private readonly IAnalysisContentStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the analysis family bridge.</summary>
    public AnalysisStudioPackageBridge(IAnalysisContentStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public StudioPackageFamily Family => StudioPackageFamily.Analysis;

    /// <inheritdoc />
    public string Format => NativeFormat;

    /// <inheritdoc />
    public bool PublishSupported => false;

    /// <inheritdoc />
    public IReadOnlyList<StudioPackageOperation> SupportedOperations => Operations;

    /// <inheritdoc />
    public IReadOnlyList<string> Limitations => BridgeLimitations;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentItemSummary>> ListItemSummariesAsync(CancellationToken cancellationToken = default)
    {
        var page = await _store.ListItemsAsync(
            new AnalysisContentItemQuery
            {
                Kind = AnalysisContentKind.AnalysisPackage,
                Limit = StudioFamilyPersistenceBridgeDefaults.MaxEnumeratedItems,
                Offset = 0,
            },
            cancellationToken).ConfigureAwait(false);
        return page.Items.Select(ToSummary).ToArray();
    }

    /// <inheritdoc />
    public async Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await ResolveItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        return new StudioContentItemPointers
        {
            ItemId = itemId,
            CurrentVersionId = VersionGuid(item.CurrentVersionId),
            // The analysis surface has no publish concept; route publication is handled by the
            // Content Publication Registry (ADR-0069 divergence).
            PublishedVersionId = null,
            OwnerId = item.OwnerId,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await ResolveItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Array.Empty<StudioContentVersion>();
        }

        var versions = await _store.ListVersionsAsync(item.ItemId, cancellationToken).ConfigureAwait(false);
        return versions
            .Where(static version => version.Kind == AnalysisContentKind.AnalysisPackage)
            .OrderBy(static version => version.Version)
            .Select(version => ToContentVersion(itemId, item, version))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var item = await ResolveItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var versions = await _store.ListVersionsAsync(item.ItemId, cancellationToken).ConfigureAwait(false);
        var match = versions.FirstOrDefault(version =>
            version.Kind == AnalysisContentKind.AnalysisPackage && VersionGuid(version.VersionId) == versionId);
        return match is null ? null : ToContentVersion(itemId, item, match);
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion> SaveVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Envelope.Body is not { ValueKind: JsonValueKind.Object } body)
        {
            throw new ArgumentException("Analysis package body must be a honua.analysis-content.v1 JSON object.");
        }

        AnalysisPackageContent content;
        try
        {
            content = JsonSerializer.Deserialize(body, AnalysisContentJsonContext.Default.AnalysisPackageContent)
                ?? throw new ArgumentException("Analysis package body is not a valid analysis package document.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Analysis package body is not a valid analysis package document.", ex);
        }

        if (content.Plan is null)
        {
            throw new ArgumentException("Analysis package body must include an executable plan.");
        }

        var rasterValidation = AnalysisContentRasterSourcePolicy.ValidateForPersistence(content, cancellationToken: cancellationToken);
        if (!rasterValidation.IsValid)
        {
            var failure = rasterValidation.Errors[0];
            throw new ArgumentException(
                $"Analysis package raster source is invalid ({failure.Code}): {failure.Message}");
        }

        var jobId = draft.Envelope.Dependencies
            .FirstOrDefault(static dependency => string.Equals(dependency.Kind, JobDependencyKind, StringComparison.OrdinalIgnoreCase))
            ?.Ref;
        var artifactIds = draft.Envelope.Dependencies
            .Where(static dependency => string.Equals(dependency.Kind, ArtifactDependencyKind, StringComparison.OrdinalIgnoreCase))
            .Select(static dependency => dependency.Ref)
            .ToArray();

        var now = _timeProvider.GetUtcNow();
        var existing = await ResolveItemAsync(draft.ItemId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var nativeItemId = NativeIdForItem(draft.ItemId);
            var version = BuildVersion(nativeItemId, 1, content, basedOnVersionId: null, jobId, artifactIds, actorId, now);
            var item = new AnalysisContentItem
            {
                ItemId = nativeItemId,
                Kind = AnalysisContentKind.AnalysisPackage,
                Name = draft.PackageKey,
                Title = null,
                OwnerId = draft.OwnerId,
                CurrentVersion = 1,
                CurrentVersionId = version.VersionId,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = actorId,
            };
            var createdItem = await _store.CreateItemAsync(item, version, cancellationToken).ConfigureAwait(false);
            return ToContentVersion(draft.ItemId, createdItem, version) with
            {
                SourceDraftId = draft.DraftId,
                ChangeNote = changeNote,
                WorkspaceId = draft.WorkspaceId,
            };
        }

        // Append with the same monotonic numbering + optimistic conflict retry the native
        // analysis content service uses.
        for (var attempt = 1; ; attempt++)
        {
            var latest = await _store.GetVersionAsync(existing.ItemId, version: null, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Analysis content item has no versions.");
            var next = BuildVersion(existing.ItemId, latest.Version + 1, content, latest.VersionId, jobId, artifactIds, actorId, now);
            try
            {
                var stored = await _store.AddVersionAsync(existing.ItemId, next, cancellationToken).ConfigureAwait(false);
                return ToContentVersion(draft.ItemId, existing, stored) with
                {
                    SourceDraftId = draft.DraftId,
                    BaseVersionId = draft.BaseVersionId,
                    ChangeNote = changeNote,
                    WorkspaceId = draft.WorkspaceId,
                };
            }
            catch (AnalysisContentStoreConflictException) when (attempt < MaxVersionCreateAttempts)
            {
                // Another writer took this version number; re-read latest and retry.
            }
        }
    }

    /// <inheritdoc />
    public Task<StudioPublicationRequest> PublishAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Publish-request is not supported for the bridged analysis family; use the Content Publication Registry for route publication.");

    private async Task<AnalysisContentItem?> ResolveItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Fast paths: bridge-created ids embed the Studio item id; console-created ids follow
        // the native "analysis-content-{guid:N}" convention whose embedded guid maps directly.
        var direct = await _store.GetItemAsync(NativeIdForItem(itemId), cancellationToken).ConfigureAwait(false)
            ?? await _store.GetItemAsync(itemId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (direct is not null && direct.Kind == AnalysisContentKind.AnalysisPackage)
        {
            return direct;
        }

        // Slow path: custom native ids re-derive through the hash mapping.
        var page = await _store.ListItemsAsync(
            new AnalysisContentItemQuery
            {
                Kind = AnalysisContentKind.AnalysisPackage,
                Limit = StudioFamilyPersistenceBridgeDefaults.MaxEnumeratedItems,
                Offset = 0,
            },
            cancellationToken).ConfigureAwait(false);
        return page.Items.FirstOrDefault(item => ItemGuid(item.ItemId) == itemId);
    }

    private static AnalysisContentVersion BuildVersion(
        string nativeItemId,
        int version,
        AnalysisPackageContent content,
        string? basedOnVersionId,
        string? jobId,
        IReadOnlyList<string> artifactIds,
        string? actorId,
        DateTimeOffset now)
        => new()
        {
            VersionId = $"{nativeItemId}:v{version.ToString(CultureInfo.InvariantCulture)}",
            ItemId = nativeItemId,
            Version = version,
            Kind = AnalysisContentKind.AnalysisPackage,
            AnalysisPackage = content,
            ContentHash = ComputeContentHash(content),
            BasedOnVersionId = basedOnVersionId,
            CreatedFromJobId = jobId,
            CreatedFromArtifactIds = artifactIds,
            CreatedAt = now,
            CreatedBy = actorId,
        };

    private static string ComputeContentHash(AnalysisPackageContent content)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(content, AnalysisContentJsonContext.Default.AnalysisPackageContent);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string NativeIdForItem(Guid itemId) => $"{NativeIdPrefix}{itemId:N}";

    private static Guid ItemGuid(string nativeId)
    {
        if (nativeId.StartsWith(NativeIdPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(nativeId[NativeIdPrefix.Length..], "N", out var embedded))
        {
            return embedded;
        }

        return StudioBridgeIds.ForNativeId(IdNamespace, nativeId);
    }

    private static Guid VersionGuid(string nativeVersionId)
        => StudioBridgeIds.Derive(VersionIdNamespace, nativeVersionId);

    private static StudioContentItemSummary ToSummary(AnalysisContentItem item) => new()
    {
        ItemId = ItemGuid(item.ItemId),
        PackageKey = SafePackageKey(item.Name, item.ItemId),
        WorkspaceId = null,
        Family = StudioPackageFamily.Analysis,
        State = StudioContentItemState.Current,
        CurrentVersionId = VersionGuid(item.CurrentVersionId),
        PublishedVersionId = null,
        OwnerId = item.OwnerId,
        CreatedBy = item.CreatedBy,
        UpdatedBy = null,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
    };

    private static StudioContentVersion ToContentVersion(Guid itemId, AnalysisContentItem item, AnalysisContentVersion version)
    {
        var dependencies = new List<StudioPackageDependency>();
        if (!string.IsNullOrWhiteSpace(version.CreatedFromJobId))
        {
            dependencies.Add(new StudioPackageDependency { Kind = JobDependencyKind, Ref = version.CreatedFromJobId, Required = false });
        }

        foreach (var artifactId in version.CreatedFromArtifactIds)
        {
            dependencies.Add(new StudioPackageDependency { Kind = ArtifactDependencyKind, Ref = artifactId, Required = false });
        }

        return new StudioContentVersion
        {
            ItemId = itemId,
            PackageKey = SafePackageKey(item.Name, item.ItemId),
            WorkspaceId = null,
            OwnerId = item.OwnerId,
            VersionId = VersionGuid(version.VersionId),
            VersionNumber = version.Version,
            ContentHash = version.ContentHash,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Analysis,
                SchemaVersion = "1.0",
                Format = NativeFormat,
                Body = JsonSerializer.SerializeToElement(version.AnalysisPackage, AnalysisContentJsonContext.Default.AnalysisPackageContent),
                Dependencies = dependencies,
                Validation = StudioValidationSummary.NotValidated,
            },
            Validation = StudioValidationSummary.NotValidated,
            Dependencies = dependencies,
            BaseVersionId = version.BasedOnVersionId is { } basedOn ? VersionGuid(basedOn) : null,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
        };
    }

    private static string SafePackageKey(string name, string nativeId)
    {
        var filtered = new string(name
            .Where(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.')
            .Take(200)
            .ToArray());
        return filtered.Length > 0 ? filtered : ItemGuid(nativeId).ToString("D");
    }
}
