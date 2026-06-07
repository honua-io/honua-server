// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Forms;

internal sealed class FormPackageLifecycleService
{
    private readonly IFormPackageStore _store;
    private readonly FormPackageValidator _validator;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<FormPackageLifecycleService> _logger;

    public FormPackageLifecycleService(
        IFormPackageStore store,
        FormPackageValidator validator,
        IAuditLog auditLog,
        ILogger<FormPackageLifecycleService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> ListAsync(HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.ListPackages");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "list-packages");

        var packages = await _store.ListPackagesAsync(context.RequestAborted).ConfigureAwait(false);
        ApplyNoStore(context.Response);
        return Results.Json(packages, FormPackageJsonContext.Default.FormPackageSummaryArray);
    }

    public async Task<IResult> CreateDraftAsync(HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.CreateDraft");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "create-draft");

        var package = await ReadPackageAsync(context).ConfigureAwait(false);
        if (package is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body must be a valid form package document.");
        }

        var actor = ResolveActor(context);
        var version = await _store.SaveDraftAsync(package, actor, context.RequestAborted).ConfigureAwait(false);
        ApplyVersionHeaders(context.Response, version, noStore: true);
        await RecordAuditAsync(context, "forms.package.save_draft", version.FormId, AuditOutcome.Success, $"{{\"version\":{version.Version}}}")
            .ConfigureAwait(false);

        FormPackageLog.DraftSaved(_logger, version.FormId, version.Version);
        return Results.Json(version, FormPackageJsonContext.Default.FormPackageVersion, statusCode: StatusCodes.Status201Created);
    }

    public async Task<IResult> GetAdminCurrentAsync(HttpContext context, string formId)
    {
        var version = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Draft, context.RequestAborted).ConfigureAwait(false)
            ?? await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, context.RequestAborted).ConfigureAwait(false);

        return version is null
            ? ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Form package '{formId}' was not found.")
            : WriteVersion(context, version, noStore: true);
    }

    public async Task<IResult> ListVersionsAsync(HttpContext context, string formId)
    {
        var versions = await _store.ListVersionsAsync(formId, context.RequestAborted).ConfigureAwait(false);
        ApplyNoStore(context.Response);
        return Results.Json(versions, FormPackageJsonContext.Default.FormPackageVersionArray);
    }

    public async Task<IResult> GetVersionAsync(HttpContext context, string formId, int version)
    {
        var packageVersion = await _store.GetVersionAsync(formId, version, context.RequestAborted).ConfigureAwait(false);
        return packageVersion is null
            ? ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Form package '{formId}' version {version} was not found.")
            : WriteVersion(context, packageVersion, noStore: true);
    }

    public async Task<IResult> UpdateDraftAsync(HttpContext context, string formId, int version)
    {
        var expectedETag = context.Request.Headers["If-Match"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expectedETag))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status428PreconditionRequired, "If-Match header is required when updating a draft.");
        }

        var package = await ReadPackageAsync(context).ConfigureAwait(false);
        if (package is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body must be a valid form package document.");
        }

        var actor = ResolveActor(context);
        var updated = await _store.UpdateDraftAsync(formId, version, package, expectedETag, actor, context.RequestAborted)
            .ConfigureAwait(false);
        if (updated is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Draft was not found, is not editable, or the ETag did not match.");
        }

        await RecordAuditAsync(context, "forms.package.update_draft", formId, AuditOutcome.Success, $"{{\"version\":{version}}}")
            .ConfigureAwait(false);
        FormPackageLog.DraftUpdated(_logger, formId, version);
        return WriteVersion(context, updated, noStore: true);
    }

    public async Task<IResult> ValidateAsync(HttpContext context, string formId, int version)
    {
        var packageVersion = await _store.GetVersionAsync(formId, version, context.RequestAborted).ConfigureAwait(false);
        if (packageVersion is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Form package '{formId}' version {version} was not found.");
        }

        var validation = await _validator.ValidateForPublishAsync(packageVersion.Package, context.RequestAborted).ConfigureAwait(false);
        if (string.Equals(packageVersion.Status, FormPackageStatus.Draft, StringComparison.OrdinalIgnoreCase))
        {
            _ = await _store.StoreValidationAsync(formId, version, validation, context.RequestAborted).ConfigureAwait(false);
        }

        FormPackageLog.PackageValidated(_logger, formId, version, validation.IsValid, validation.Issues.Length);
        ApplyNoStore(context.Response);
        return Results.Json(validation, FormPackageJsonContext.Default.FormPackageValidationResult);
    }

    public async Task<IResult> PublishAsync(HttpContext context, string formId, int version)
    {
        var packageVersion = await _store.GetVersionAsync(formId, version, context.RequestAborted).ConfigureAwait(false);
        if (packageVersion is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Form package '{formId}' version {version} was not found.");
        }

        var validation = await _validator.ValidateForPublishAsync(packageVersion.Package, context.RequestAborted).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            _ = await _store.StoreValidationAsync(formId, version, validation, context.RequestAborted).ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.package.publish", formId, AuditOutcome.Failure, $"{{\"version\":{version},\"issueCount\":{validation.Issues.Length}}}")
                .ConfigureAwait(false);
            return Results.Json(validation, FormPackageJsonContext.Default.FormPackageValidationResult, statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = ResolveActor(context);
        var published = await _store.PublishAsync(formId, version, validation, actor, context.RequestAborted).ConfigureAwait(false);
        if (published is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Only draft form package versions can be published.");
        }

        await RecordAuditAsync(context, "forms.package.publish", formId, AuditOutcome.Success, $"{{\"version\":{version}}}")
            .ConfigureAwait(false);
        FormPackageLog.PackagePublished(_logger, formId, version);
        return WriteVersion(context, published, noStore: true);
    }

    public async Task<IResult> ReopenAsync(HttpContext context, string formId, int version)
    {
        var actor = ResolveActor(context);
        var draft = await _store.ReopenAsync(formId, version, actor, context.RequestAborted).ConfigureAwait(false);
        if (draft is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' version {version} was not found.");
        }

        await RecordAuditAsync(context, "forms.package.reopen", formId, AuditOutcome.Success, $"{{\"fromVersion\":{version},\"draftVersion\":{draft.Version}}}")
            .ConfigureAwait(false);
        FormPackageLog.PackageReopened(_logger, formId, version, draft.Version);
        return WriteVersion(context, draft, noStore: true);
    }

    /// <summary>
    /// Computes the offline compatibility and migration manifest for a published
    /// form package. Offline clients call this with their locally-cached version
    /// (<paramref name="clientVersion"/>) to learn whether pending offline edits
    /// can be submitted against the current published version, whether a refresh
    /// is recommended, or whether migration is required before submitting.
    /// </summary>
    public async Task<IResult> GetCompatibilityAsync(HttpContext context, string formId, int? clientVersion)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.GetCompatibility");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "get-compatibility");
        activity?.SetTag("honua.form_id", formId);

        var current = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, context.RequestAborted).ConfigureAwait(false);
        if (current is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' was not found.");
        }

        FormPackageVersion? client = null;
        if (clientVersion is { } requested && requested != current.Version)
        {
            client = await _store.GetVersionAsync(formId, requested, context.RequestAborted).ConfigureAwait(false);
        }

        var manifest = BuildCompatibilityManifest(formId, clientVersion, current, client);
        activity?.SetTag("honua.compatibility", manifest.Compatibility);
        ApplyNoStore(context.Response);
        return Results.Json(manifest, FormPackageJsonContext.Default.FormCompatibilityManifest);
    }

    private static FormCompatibilityManifest BuildCompatibilityManifest(
        string formId,
        int? clientVersion,
        FormPackageVersion current,
        FormPackageVersion? client)
    {
        // No client version supplied, or the client is already on the current
        // published version: nothing to migrate.
        if (clientVersion is null || clientVersion == current.Version)
        {
            return new FormCompatibilityManifest
            {
                FormId = formId,
                ClientVersion = clientVersion,
                CurrentPublishedVersion = current.Version,
                Compatibility = FormCompatibilityLevel.Current,
                OfflineEditsSubmittable = true,
                RefreshRecommended = false,
                MigrationRequired = false,
                ClientContentHash = current.ContentHash,
                CurrentContentHash = current.ContentHash,
                ClientPolicyHash = current.PolicyHash,
                CurrentPolicyHash = current.PolicyHash,
                MigrationSignals = []
            };
        }

        // The client references a version the server no longer knows (purged,
        // never existed, or never published): clients must re-provision.
        if (client is null)
        {
            return new FormCompatibilityManifest
            {
                FormId = formId,
                ClientVersion = clientVersion,
                CurrentPublishedVersion = current.Version,
                Compatibility = FormCompatibilityLevel.Unknown,
                OfflineEditsSubmittable = false,
                RefreshRecommended = true,
                MigrationRequired = true,
                CurrentContentHash = current.ContentHash,
                CurrentPolicyHash = current.PolicyHash,
                MigrationSignals =
                [
                    new FormMigrationSignal
                    {
                        Code = "versionUnknown",
                        Severity = "breaking",
                        Message = $"Version {clientVersion} is unknown or no longer available; re-provision the form before submitting."
                    }
                ]
            };
        }

        var signals = new List<FormMigrationSignal>();

        var targetChanged =
            !string.Equals(client.Package.Target?.ServiceId, current.Package.Target?.ServiceId, StringComparison.Ordinal)
            || (client.Package.Target?.LayerId ?? -1) != (current.Package.Target?.LayerId ?? -1);
        if (targetChanged)
        {
            signals.Add(new FormMigrationSignal
            {
                Code = "targetChanged",
                Severity = "breaking",
                Message = "The submission target service/layer changed; offline edits may be rejected."
            });
        }

        var policyChanged = !string.Equals(client.PolicyHash, current.PolicyHash, StringComparison.Ordinal);
        if (policyChanged)
        {
            signals.Add(new FormMigrationSignal
            {
                Code = "policyChanged",
                Severity = "breaking",
                Message = "Submit, attachment, privacy, or offline policy changed; re-validate offline edits before submitting."
            });
        }

        var contentChanged = !string.Equals(client.ContentHash, current.ContentHash, StringComparison.Ordinal);
        if (contentChanged && !policyChanged && !targetChanged)
        {
            signals.Add(new FormMigrationSignal
            {
                Code = "contentChanged",
                Severity = "info",
                Message = "Form fields or layout changed without affecting submission policy; refresh when convenient."
            });
        }

        var breaking = targetChanged || policyChanged;
        var compatibility = breaking
            ? FormCompatibilityLevel.Breaking
            : FormCompatibilityLevel.Compatible;

        return new FormCompatibilityManifest
        {
            FormId = formId,
            ClientVersion = clientVersion,
            CurrentPublishedVersion = current.Version,
            Compatibility = compatibility,
            OfflineEditsSubmittable = !breaking,
            RefreshRecommended = true,
            MigrationRequired = breaking,
            ClientContentHash = client.ContentHash,
            CurrentContentHash = current.ContentHash,
            ClientPolicyHash = client.PolicyHash,
            CurrentPolicyHash = current.PolicyHash,
            MigrationSignals = signals.ToArray()
        };
    }

    public async Task<IResult> GetRuntimeCurrentAsync(HttpContext context, string formId)
    {
        var version = await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, context.RequestAborted).ConfigureAwait(false);
        return version is null
            ? ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' was not found.")
            : WriteVersion(context, version, noStore: false);
    }

    public async Task<IResult> GetRuntimeVersionAsync(HttpContext context, string formId, int version)
    {
        var packageVersion = await _store.GetVersionAsync(formId, version, context.RequestAborted).ConfigureAwait(false);
        if (packageVersion is null || !string.Equals(packageVersion.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' version {version} was not found.");
        }

        return WriteVersion(context, packageVersion, noStore: false);
    }

    private static async Task<FormPackageDocument?> ReadPackageAsync(HttpContext context)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    FormPackageJsonContext.Default.FormPackageDocument,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult WriteVersion(HttpContext context, FormPackageVersion version, bool noStore)
    {
        ApplyVersionHeaders(context.Response, version, noStore);
        return Results.Json(version, FormPackageJsonContext.Default.FormPackageVersion);
    }

    private static void ApplyVersionHeaders(HttpResponse response, FormPackageVersion version, bool noStore)
    {
        response.Headers.ETag = version.ETag;
        if (noStore)
        {
            ApplyNoStore(response);
        }
        else
        {
            response.Headers.CacheControl = "private, max-age=60, must-revalidate";
        }
    }

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string ResolveActor(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.Identity?.Name
           ?? AuditEvent.AnonymousActor;

    private Task RecordAuditAsync(
        HttpContext context,
        string action,
        string formId,
        AuditOutcome outcome,
        string details)
        => _auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ResolveActor(context),
            ActorType = context.User.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "form_package",
            ResourceId = formId,
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            Details = details
        }, context.RequestAborted);
}

internal static partial class FormPackageLog
{
    [LoggerMessage(EventId = 118400, Level = LogLevel.Information, Message = "Saved form package draft {FormId} version {Version}.")]
    public static partial void DraftSaved(ILogger logger, string formId, int version);

    [LoggerMessage(EventId = 118401, Level = LogLevel.Information, Message = "Updated form package draft {FormId} version {Version}.")]
    public static partial void DraftUpdated(ILogger logger, string formId, int version);

    [LoggerMessage(EventId = 118402, Level = LogLevel.Information, Message = "Validated form package {FormId} version {Version}; isValid={IsValid}, issueCount={IssueCount}.")]
    public static partial void PackageValidated(ILogger logger, string formId, int version, bool isValid, int issueCount);

    [LoggerMessage(EventId = 118403, Level = LogLevel.Information, Message = "Published form package {FormId} version {Version}.")]
    public static partial void PackagePublished(ILogger logger, string formId, int version);

    [LoggerMessage(EventId = 118404, Level = LogLevel.Information, Message = "Reopened form package {FormId} published version {PublishedVersion} as draft version {DraftVersion}.")]
    public static partial void PackageReopened(ILogger logger, string formId, int publishedVersion, int draftVersion);
}
