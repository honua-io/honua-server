// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services.Bridging;

/// <summary>
/// Bridges the Studio <c>form</c> package family onto the native forms package store
/// (ADR-0069, honua-server#3004). Envelope bodies are native <c>honua.form-package.v1</c>
/// documents serialized with the family's own <see cref="FormPackageJsonContext"/>, so JSON
/// value kinds, <c>ruleId</c>s, and domain codes round-trip exactly as they do through
/// <c>/api/v1/admin/forms/packages</c>. Saves append a new native draft version (never an
/// in-place overwrite), so console editors using ETag/If-Match are never clobbered; publishes
/// run the native <see cref="FormPackageValidator"/> publish gate.
/// </summary>
public sealed class FormStudioPackageBridge : IStudioFamilyPersistenceBridge
{
    private const string IdNamespace = "form";
    private const string VersionIdNamespace = "form-version";

    /// <summary>Native package format for bridged form packages.</summary>
    public const string NativeFormat = "honua.form-package.v1";

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
        StudioPackageOperation.PublishRequestCreate,
        StudioPackageOperation.Reopen,
    ];

    private static readonly string[] BridgeLimitations =
    [
        "form packages persist through the native forms package store (/api/v1/admin/forms/packages); a content version saved through the lifecycle is a native draft version and remains natively editable until published",
        "bridged form items appear in content-item enumeration once first saved; unsaved drafts are listed under /studio/package-drafts",
        "content-item enumeration merges at most 1000 bridged form packages",
        "rollback is not supported for bridged form packages; reopen the target version and republish instead",
    ];

    private readonly IFormPackageStore _store;
    private readonly FormPackageValidator _validator;

    /// <summary>Initializes the form family bridge.</summary>
    public FormStudioPackageBridge(IFormPackageStore store, FormPackageValidator validator)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(validator);
        _store = store;
        _validator = validator;
    }

    /// <inheritdoc />
    public StudioPackageFamily Family => StudioPackageFamily.Form;

    /// <inheritdoc />
    public string Format => NativeFormat;

    /// <inheritdoc />
    public bool PublishSupported => true;

    /// <inheritdoc />
    public IReadOnlyList<StudioPackageOperation> SupportedOperations => Operations;

    /// <inheritdoc />
    public IReadOnlyList<string> Limitations => BridgeLimitations;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentItemSummary>> ListItemSummariesAsync(CancellationToken cancellationToken = default)
    {
        var packages = await _store.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        return packages
            .Take(StudioFamilyPersistenceBridgeDefaults.MaxEnumeratedItems)
            .Select(ToSummary)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var formId = await ResolveFormIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (formId is null)
        {
            return null;
        }

        var draft = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Draft, cancellationToken).ConfigureAwait(false);
        var published = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, cancellationToken).ConfigureAwait(false);
        var current = draft ?? published;
        if (current is null)
        {
            return null;
        }

        return new StudioContentItemPointers
        {
            ItemId = itemId,
            CurrentVersionId = VersionGuid(formId, current.Version),
            PublishedVersionId = published is null ? null : VersionGuid(formId, published.Version),
            // Forms has no ownership model; a null owner fails closed under the #3018
            // authorization model (admin-only), matching the native surface's posture.
            OwnerId = null,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var formId = await ResolveFormIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (formId is null)
        {
            return Array.Empty<StudioContentVersion>();
        }

        var versions = await _store.ListVersionsAsync(formId, cancellationToken).ConfigureAwait(false);
        return versions
            .OrderBy(static version => version.Version)
            .Select(version => ToContentVersion(itemId, formId, version))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
        return resolved is not { } match ? null : ToContentVersion(itemId, match.FormId, match.Version);
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
            throw new ArgumentException("Form package body must be a honua.form-package.v1 JSON object.");
        }

        var existingFormId = await ResolveFormIdAsync(draft.ItemId, cancellationToken).ConfigureAwait(false);
        var formId = existingFormId ?? draft.ItemId.ToString("D");

        FormPackageDocument document;
        try
        {
            // Stamp the server-resolved form id so the native record and the Studio item id stay
            // one-to-one regardless of what the envelope body carried; everything else in the
            // body round-trips through the family's own serializer contract.
            var node = JsonNode.Parse(body.GetRawText()) ?? throw new ArgumentException("Form package body could not be parsed.");
            node["formId"] = formId;
            document = JsonSerializer.Deserialize(node, FormPackageJsonContext.Default.FormPackageDocument)
                ?? throw new ArgumentException("Form package body is not a valid honua.form-package.v1 document.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Form package body is not a valid honua.form-package.v1 document.", ex);
        }

        var saved = await _store.SaveDraftAsync(document, actorId ?? "studio-lifecycle", cancellationToken).ConfigureAwait(false);
        return ToContentVersion(draft.ItemId, saved.FormId, saved) with
        {
            SourceDraftId = draft.DraftId,
            BaseVersionId = draft.BaseVersionId,
            ChangeNote = changeNote,
            WorkspaceId = draft.WorkspaceId,
        };
    }

    /// <inheritdoc />
    public async Task<StudioPublicationRequest> PublishAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveVersionAsync(request.ItemId, request.VersionId, cancellationToken).ConfigureAwait(false);
        if (resolved is not { } match)
        {
            throw new KeyNotFoundException("Studio content version was not found.");
        }

        var validation = await _validator.ValidateForPublishAsync(match.Version.Package, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            _ = await _store.StoreValidationAsync(match.FormId, match.Version.Version, validation, cancellationToken).ConfigureAwait(false);
            return request with
            {
                Status = StudioPublicationRequestStatus.Rejected,
                Validation = MapValidation(validation),
            };
        }

        var published = await _store.PublishAsync(
            match.FormId,
            match.Version.Version,
            validation,
            request.RequestedBy ?? "studio-lifecycle",
            cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            throw new InvalidOperationException("Only draft form package versions can be published; reopen the version to create a new draft.");
        }

        return request with
        {
            Status = StudioPublicationRequestStatus.Accepted,
            Validation = MapValidation(validation),
        };
    }

    private async Task<string?> ResolveFormIdAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // Fast path: lifecycle-created packages use the Studio item id as the native form id.
        var directId = itemId.ToString("D");
        var directVersions = await _store.ListVersionsAsync(directId, cancellationToken).ConfigureAwait(false);
        if (directVersions.Length > 0)
        {
            return directId;
        }

        // Console-created form ids are arbitrary strings; reverse-resolve by re-deriving.
        var packages = await _store.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        return packages.FirstOrDefault(package => ItemGuid(package.FormId) == itemId)?.FormId;
    }

    private async Task<(string FormId, FormPackageVersion Version)?> ResolveVersionAsync(
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var formId = await ResolveFormIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (formId is null)
        {
            return null;
        }

        var versions = await _store.ListVersionsAsync(formId, cancellationToken).ConfigureAwait(false);
        var match = versions.FirstOrDefault(version => VersionGuid(formId, version.Version) == versionId);
        return match is null ? null : (formId, match);
    }

    private static Guid ItemGuid(string formId) => StudioBridgeIds.ForNativeId(IdNamespace, formId);

    private static Guid VersionGuid(string formId, int version)
        => StudioBridgeIds.Derive(VersionIdNamespace, $"{formId}:{version}");

    private static StudioContentItemSummary ToSummary(FormPackageSummary package)
    {
        var currentVersion = package.CurrentDraftVersion ?? package.CurrentPublishedVersion;
        return new StudioContentItemSummary
        {
            ItemId = ItemGuid(package.FormId),
            PackageKey = SafePackageKey(package.FormId),
            WorkspaceId = null,
            Family = StudioPackageFamily.Form,
            State = package.CurrentPublishedVersion is not null
                ? StudioContentItemState.Published
                : StudioContentItemState.Current,
            CurrentVersionId = currentVersion is { } current ? VersionGuid(package.FormId, current) : null,
            PublishedVersionId = package.CurrentPublishedVersion is { } published ? VersionGuid(package.FormId, published) : null,
            OwnerId = null,
            CreatedBy = null,
            UpdatedBy = null,
            CreatedAt = package.UpdatedAt,
            UpdatedAt = package.UpdatedAt,
        };
    }

    private static StudioContentVersion ToContentVersion(Guid itemId, string formId, FormPackageVersion version)
    {
        var validation = MapValidation(version.Validation);
        return new StudioContentVersion
        {
            ItemId = itemId,
            PackageKey = SafePackageKey(formId),
            WorkspaceId = null,
            OwnerId = null,
            VersionId = VersionGuid(formId, version.Version),
            VersionNumber = version.Version,
            ContentHash = version.ContentHash,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Form,
                SchemaVersion = "1.0",
                Format = NativeFormat,
                Body = JsonSerializer.SerializeToElement(version.Package, FormPackageJsonContext.Default.FormPackageDocument),
                Validation = validation,
            },
            Validation = validation,
            BaseVersionId = version.ReopenedFromVersion is { } reopened ? VersionGuid(formId, reopened) : null,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
        };
    }

    private static StudioValidationSummary MapValidation(FormPackageValidationResult? validation)
    {
        if (validation is null)
        {
            return StudioValidationSummary.NotValidated;
        }

        var diagnostics = validation.Issues
            .Select(static issue => new StudioValidationDiagnostic
            {
                Code = issue.Code,
                Severity = issue.Severity.ToLowerInvariant() switch
                {
                    "error" => StudioPackageDiagnosticSeverity.Error,
                    "warning" => StudioPackageDiagnosticSeverity.Warning,
                    _ => StudioPackageDiagnosticSeverity.Info,
                },
                Path = issue.Path ?? issue.FieldId,
                Message = issue.Message,
            })
            .ToArray();

        var status = !validation.IsValid
            ? StudioPackageValidationStatus.Invalid
            : diagnostics.Any(static diagnostic => diagnostic.Severity == StudioPackageDiagnosticSeverity.Warning)
                ? StudioPackageValidationStatus.Warning
                : StudioPackageValidationStatus.Valid;

        return new StudioValidationSummary
        {
            Status = status,
            Diagnostics = diagnostics,
            GeneratedAt = null,
        };
    }

    private static string SafePackageKey(string nativeId)
    {
        var filtered = new string(nativeId
            .Where(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.')
            .Take(200)
            .ToArray());
        return filtered.Length > 0 ? filtered : ItemGuid(nativeId).ToString("D");
    }
}
