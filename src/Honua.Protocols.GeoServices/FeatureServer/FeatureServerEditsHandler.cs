// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Collaboration;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Handler for FeatureServer edit operations with explicit geometry validation.
/// </summary>
internal sealed class FeatureServerEditsHandler(
    FeatureServerEditsDependencies dependencies,
    ILogger<FeatureServerEditsHandler> logger)
{
    private const int MaxSafeEditErrorMessageLength = 240;
    private const string InvalidFeatureDataMessage = "Invalid feature data.";
    private const string InvalidGeometryPayloadMessage = "Invalid geometry payload.";

    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly IFeatureServerGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly IEditParameterAdapter<GeoServicesEditRequest> _editParameterAdapter = dependencies.EditParameterAdapter;
    private readonly IEditProcessor _editProcessor = dependencies.EditProcessor;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly IHttpContextAccessor _httpContextAccessor = dependencies.HttpContextAccessor;
    private readonly FeatureMutationEventService _mutationEventService = dependencies.MutationEventService;
    private readonly IPluginEditPipeline _pluginPipeline = dependencies.PluginPipeline;
    private readonly IApplyEditsIdempotencyStore _idempotencyStore = dependencies.IdempotencyStore;
    private readonly IFeatureEditGuard _editGuard = dependencies.EditGuard;
    private readonly IFeatureLockService _featureLocks = dependencies.FeatureLocks;
    private readonly ILogger<FeatureServerEditsHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles applyEdits requests for adding, updating, and deleting features.
    /// </summary>
    public async Task<IResult> HandleApplyEditsAsync(
        string serviceId,
        int layerId,
        ApplyEditsRequest request,
        Honua.Core.Configuration.EditLimits editLimits,
        ApplyEditsIdempotencyChoice? idempotencyOverride = null,
        AmbientFeatureEditTransaction? ambientTransaction = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext!;

        // FeatureServer editing is a Pro entitlement (#1591) scoped to the Esri GeoServices
        // surface only — open-protocol edits are Community. All FeatureServer write
        // entrypoints (applyEdits/add/update/delete and service-level applyEdits) funnel through
        // this shared handler, so the gate is enforced once here for the whole GeoServices surface.
        var editsGate = LicenseGate.RequireEntitlement(
            httpContext, FeatureCatalog.FeatureServerEditsKey, "FeatureServer editing", _logger);
        if (editsGate is not null)
        {
            return editsGate;
        }

        // Server-side at-most-once (#2250): validate the optional Idempotency-Key header up front so a
        // malformed header fails fast with a 400 before any edit work, rather than being silently ignored.
        // An explicit override (calculate batches) does not re-read the shared header.
        string? idempotencyKey;
        if (idempotencyOverride is { } idempotencyChoice)
        {
            idempotencyKey = idempotencyChoice.Key;
        }
        else if (!ApplyEditsIdempotency.TryResolveKey(httpContext, out idempotencyKey, out var idempotencyError))
        {
            return StandardErrorHelpers.CreateBadRequest(httpContext, idempotencyError!);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "applyEdits",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            httpContext.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        // Tracks the idempotency reservation this request owns (#3052) and the token that proves
        // ownership. Set the moment TryReserveAsync wins; the finally block below releases it
        // unless the write may have committed rows, so no post-reservation exit — rejection,
        // rollback, cancellation before dispatch, or a confirmed zero-row response — can leave the
        // key pinned and turn a genuine client retry into an idempotency conflict.
        ApplyEditsIdempotencyScope? heldReservation = null;
        string? heldReservationToken = null;

        // Whether this request may have put rows in the database. It is deliberately NOT derived
        // from the ExecuteEdits return value: when rollbackOnFailure=false the writer
        // commits rows independently, so a cancellation or transport fault part-way through the
        // batch unwinds with earlier rows already committed and no result to inspect. The flag is
        // therefore raised inside ExecuteEdits immediately BEFORE the write is dispatched and only
        // lowered once the outcome is known to be "nothing committed".
        //
        // The bias is deliberate: retaining a reservation costs one spurious 409 until the window
        // expires, releasing one wrongly costs duplicate rows.
        var writeOutcome = new EditWriteOutcome();

        try
        {
            FeatureServerLog.ApplyEditsRequested(_logger, serviceId, layerId,
                request.Adds?.Length ?? 0,
                request.Updates?.Length ?? 0,
                request.Deletes?.Length ?? 0);

            // Validate service and layer exist
            var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
                _resourceValidator,
                serviceId,
                layerId,
                httpContext,
                _logger,
                cancellationToken);
            if (!validationResult.IsValid)
            {
                return validationResult.ErrorResult!;
            }

            var service = validationResult.Service!;
            var publication = validationResult.Publication!;
            var resource = validationResult.Resource!;
            // BH2-012: Use OR-style coarse gate — allow if the principal holds any one of
            // Update, Insert, or Delete so a Delete-only or Insert-only RBAC grant holder
            // is not rejected before the request body has been inspected. Per-type checks
            // below (lines ~172-190) remain the authoritative per-operation enforcers.
            var accessError = await AccessPolicyHelpers.RequireAnyMutatingOperationAccessAsync(
                httpContext, resource, service, cancellationToken).ConfigureAwait(false);
            if (accessError != null)
            {
                return accessError;
            }

            var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                httpContext,
                resource,
                service,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }

            var snapshotProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var storageLayerId = ResolveStorageLayerIdV2(snapshot, publication, resource);
            if (storageLayerId is null)
            {
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Layer '{resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
            }

            // Validate edit limits
            var limitsValidationResult = ValidateEditLimits(request, editLimits, httpContext);
            if (limitsValidationResult != null)
            {
                return limitsValidationResult;
            }

            // Resolve the target branch version (#1272, ADR-0051). Absent / SDE.DEFAULT resolves to
            // VersionContext.Default — the byte-identical non-versioned write path. A named version
            // is Pro-gated and Postgres-only.
            var (versionContext, versionError) = await FeatureServerVersioning.ResolveEditVersionAsync(
                httpContext, request.GdbVersion, cancellationToken).ConfigureAwait(false);
            if (versionError != null)
            {
                return versionError;
            }

            var totalCount = (request.Adds?.Length ?? 0) + (request.Updates?.Length ?? 0) + (request.Deletes?.Length ?? 0);
            if (totalCount == 0)
            {
                return Results.Json(new ApplyEditsResponse
                {
                    AddResults = [],
                    UpdateResults = [],
                    DeleteResults = [],
                    Success = true
                },
                    FeatureServerJsonContext.Default.ApplyEditsResponse,
                    contentType: "application/json");
            }

            // Metadata capabilities are an editing contract, not merely advisory text. Reject
            // an edit kind that the published service did not declare instead of allowing the
            // handler to expose a broader surface than Esri clients were told exists (#4073).
            var undeclaredOperation = request.Adds?.Length > 0 && !FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Create", publication)
                ? "Create"
                : request.Updates?.Length > 0 && !FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Update", publication)
                    ? "Update"
                    : request.Deletes?.Length > 0 && !FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Delete", publication)
                        ? "Delete"
                        : null;
            if (undeclaredOperation is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    $"{undeclaredOperation} edits are not enabled for this service",
                    [$"Declare the {undeclaredOperation} capability before submitting {undeclaredOperation.ToLowerInvariant()} edits."]);
            }

            // Per-edit-type authorization checks (BH-002 / BH3-001 / BH3-014): the pre-body gate
            // uses Update as the coarsest write check; after the body is read, each present edit
            // type is gated on its specific operation so a delete-only payload requires Delete,
            // an insert-only payload requires Insert, and an updates-only payload requires Update.
            // ALL THREE checks are required — omitting any one allows a principal that holds only
            // a different operation's grant to bypass the missing check entirely.
            //
            // RequireResourceDataEditorAsync is called here with a specific requiredOperation so
            // that the RBAC data-editor gate bypass is narrowed to that exact operation: a caller
            // with only an Insert grant cannot bypass the gate for an Update-only payload
            // (BH3-001) and a Delete-only grantee cannot do the same (BH3-014).
            if (request.Adds?.Length > 0)
            {
                var insertError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                    httpContext, resource, service, AuthorizationOperation.Insert, cancellationToken).ConfigureAwait(false);
                if (insertError != null)
                {
                    return insertError;
                }
            }

            if (request.Updates?.Length > 0)
            {
                var updateError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                    httpContext, resource, service, AuthorizationOperation.Update, cancellationToken).ConfigureAwait(false);
                if (updateError != null)
                {
                    return updateError;
                }
            }

            if (request.Deletes?.Length > 0)
            {
                var deleteError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                    httpContext, resource, service, AuthorizationOperation.Delete, cancellationToken).ConfigureAwait(false);
                if (deleteError != null)
                {
                    return deleteError;
                }
            }

            // Resolve the authenticated principal once so owner-based edit policies
            // (ownership-based access control, #2132) are enforced consistently in the
            // shared edit pipeline rather than by caller discipline.
            var editPrincipal = ResolveEditPrincipal(httpContext);

            // At-most-once replay (#2250): a retry carrying a previously-seen Idempotency-Key returns the
            // original response without re-applying the edit, so a retried add cannot create a duplicate
            // feature. The key is scoped to (principal, service, layer) inside the store.
            ApplyEditsIdempotencyScope? idempotencyScope = idempotencyKey is null
                ? null
                : new ApplyEditsIdempotencyScope(
                    serviceId,
                    layerId,
                    string.IsNullOrEmpty(editPrincipal.Name) ? "anonymous" : editPrincipal.Name,
                    idempotencyKey);

            if (idempotencyScope is { } replayScope)
            {
                var replay = await _idempotencyStore.TryGetAsync(replayScope, cancellationToken).ConfigureAwait(false);
                if (replay is not null)
                {
                    FeatureServerLog.ApplyEditsReplayed(_logger, serviceId, layerId);
                    scope.SetSuccess(0);
                    return Results.Json(replay, FeatureServerJsonContext.Default.ApplyEditsResponse,
                        contentType: "application/json");
                }

                // Atomic reserve: only one concurrent request carrying the same Idempotency-Key may
                // proceed to execute the edit. The loser returns 409 so the client can retry after the
                // winner has written the response; the retry will hit the replay path above (#2250, BH5-001).
                var reservationToken = await _idempotencyStore.TryReserveAsync(replayScope, cancellationToken).ConfigureAwait(false);
                if (reservationToken is null)
                {
                    FeatureServerLog.ApplyEditsIdempotencyConflict(_logger, serviceId, layerId);
                    // Routed through the shared error helper rather than the previous
                    // Results.Conflict(new { error = "..." }) so the conflict is shaped like every
                    // other GeoServices error (Esri envelope carrying code 409) and is serialized by
                    // a source-generated context. The anonymous type it replaces cannot be
                    // serialized at all once reflection-based serialization is off — Honua.Server
                    // sets JsonSerializerIsReflectionEnabledByDefault=false — so this branch's
                    // payload was the one applyEdits response that was neither AOT-safe nor
                    // protocol-shaped (#3052).
                    return StandardErrorHelpers.CreateConflict(httpContext,
                        "A concurrent request with the same Idempotency-Key is already being processed. Retry after a brief delay.");
                }

                heldReservation = replayScope;
                heldReservationToken = reservationToken;
                // The caller commits or rolls back. Keep the reservation on the ambient
                // transaction so a later batch failure can release it instead of replaying
                // a write that never committed.
                ambientTransaction?.HeldReservations.Add((replayScope, reservationToken));
            }

            // Partial updates are assembled from a read snapshot. Guard ordinary single-row
            // updates with that snapshot's token, then re-read, merge and validate again only
            // after a confirmed precondition failure. Never retry an ambiguous commit or a
            // caller-supplied precondition, and never replay a multi-operation request.
            // Collaborative-editing lock enforcement (#4402). A lease handed out by
            // /collaboration/feature-locks is binding on this write path: an update or
            // delete of a feature another editor currently holds is rejected with the
            // stable per-feature FeatureLocked code instead of silently overwriting the
            // holder. The scope is null — and every per-feature check is skipped — when
            // no lease is held anywhere, which is the uncontended default.
            var lockScope = await ResolveEditLockScopeAsync(
                httpContext, service, publication, layerId, cancellationToken).ConfigureAwait(false);

            var retryStaleUpdate = request.Updates is { Length: 1 } &&
                request.Adds is not { Length: > 0 } && request.Deletes is not { Length: > 0 } &&
                request.Preconditions.IsDefaultOrEmpty && versionContext is not { IsDefault: false };
            EditOperationContext editContext;
            FeatureEditResult editResult;
            for (var attempt = 0; ; attempt++)
            {
                editContext = await ProcessEditOperationsAsync(
                    request,
                    resource,
                    storageLayerId.Value,
                    editPrincipal,
                    lockScope,
                    retryStaleUpdate,
                    versionContext,
                    cancellationToken);
                editContext.GuardUpdateSnapshot = retryStaleUpdate;
                await ApplyPluginEditPipelineAsync(serviceId, layerId, resource, editContext, cancellationToken)
                    .ConfigureAwait(false);
                if (editContext.HasValidationErrors && request.RollbackOnFailure)
                {
                    return CreateRollbackResponse(editContext, serviceId, layerId);
                }

                editResult = await ExecuteEdits(
                    storageLayerId.Value,
                    resource,
                    editContext,
                    request,
                    serviceId,
                    versionContext,
                    writeOutcome,
                    cancellationToken,
                    ambientTransaction?.Transaction);
                writeOutcome.MayHaveCommitted = editResult.MayHaveCommitted;
                if (!retryStaleUpdate || attempt >= 15 || editResult.MayHaveCommitted ||
                    editResult.UpdateResults is not { Length: 1 } ||
                    editResult.UpdateResults[0].ErrorCode != EditOperationResult.PreconditionFailedErrorCode)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            var editCommittedRows = !editResult.WasRolledBack &&
                (editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount) > 0;

            // A returned zero count is not proof that nothing landed: a provider can convert an
            // ambiguous auto-commit/acknowledgement failure into a failed per-row result. Use the
            // provider's explicit commit-outcome signal as well as successful counts.
            writeOutcome.MayHaveCommitted = editResult.MayHaveCommitted;

            if (editCommittedRows && ambientTransaction is null)
            {
                await RunPostCommitSideEffectsAsync(
                    serviceId, layerId, resource, editContext, cancellationToken).ConfigureAwait(false);
            }
            else if (editCommittedRows)
            {
                var committedContext = editContext;
                var committedResource = resource;
                ambientTransaction!.AfterCommit.Add(ct =>
                    RunPostCommitSideEffectsAsync(serviceId, layerId, committedResource, committedContext, ct));
            }

            // Build and return final response
            var featureCount = editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount;
            scope.SetSuccess(featureCount);
            var finalResponse = BuildFinalResponse(editContext, editResult);
            // The edit moment is taken once the write has returned and travels with the response the
            // idempotency store records below, so a replayed retry reports when the edits were
            // originally applied (#4105). The endpoint emits it only when returnEditMoment=true.
            finalResponse.EditMoment = (httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System)
                .GetUtcNow()
                .ToUnixTimeMilliseconds();
            FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, finalResponse.Success);

            // Record the response for at-most-once replay (#2250) only when the edit actually committed
            // rows. A fully-failed/no-op request is intentionally not recorded so a genuine retry is
            // re-attempted rather than replaying a no-op failure. Best-effort: the store swallows errors.
            if (idempotencyScope is { } recordScope && heldReservationToken is { } recordToken && editCommittedRows)
            {
                // The reservation is replaced by the recorded response rather than released: a
                // successful key stays occupied for the whole dedupe window so a duplicate retry
                // replays this response instead of re-applying the edit (#2250). The finally block
                // below skips the release for exactly this reason (writeOutcome.MayHaveCommitted).
                // The response is stored only while this request still owns the reservation.
                // An ambient calculate transaction has not committed yet. Recording now would make
                // a rolled-back batch replay as success and skip the write on retry (#6359).
                if (ambientTransaction is null)
                {
                    await _idempotencyStore.SetAsync(recordScope, recordToken, finalResponse, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    ambientTransaction.Recorded.Add((recordScope, recordToken, finalResponse));
                }
            }

            return Results.Json(finalResponse, FeatureServerJsonContext.Default.ApplyEditsResponse,
                contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EditBatchValidationException ex)
        {
            // A batch-level validation failure is a client error. Mapping it to 500 made one
            // bad add fail the whole request, including rows that rollbackOnFailure=false
            // should have kept (#6133). Per-slot failures are reported on the edit result
            // instead; this is the leftover that never reached a slot.
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateBadRequest(
                httpContext,
                SanitizeEditErrorMessage(ex.Message, "Invalid edit request."));
        }
        // A read/query-only provider's writer (ReadOnlyFeatureWriter for DuckDB and
        // MySQL/MariaDB) rejects every write with NotSupportedException. That is the
        // provider's documented posture, not an internal failure, so map it through the
        // shared exception mapping (405 Method Not Allowed) instead of a generic 500 —
        // mirroring the explicit NotSupportedException handling the OGC API Features
        // transaction handlers already do (honua-server#2983).
        catch (NotSupportedException ex)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateFromException(httpContext, ex);
        }
        // Intentional catch-all request-handling boundary: logs and returns a GeoServices-format
        // 500 rather than letting an unmapped provider/store exception crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Apply edits failed");
        }
        finally
        {
            // Release the reservation only when this request provably put nothing in the database
            // (#3052). That covers every post-reservation exit that recorded no replay value and
            // committed nothing: a rejection or cancellation before the write was dispatched, the
            // read-only provider's blanket NotSupportedException, the rollbackOnFailure validation
            // response, a rolled-back batch, and a confirmed zero-row response (which
            // deliberately does not record so a genuine retry is re-attempted — the retry it must
            // not then block with a conflict).
            //
            // Two cases deliberately KEEP the key. An edit that committed rows, so a retry can never
            // duplicate them whether or not the response was recorded. And an edit whose write was
            // dispatched but whose outcome is unknown — a cancellation between the per-row commits
            // the explicit rollbackOnFailure=false mode performs, a transport fault mid-batch — because
            // rows may already exist and re-running them would duplicate.
            //
            // ReleaseAsync takes no cancellation token on purpose, so cancellation before dispatch
            // can still free its key. An ambiguous cancellation after dispatch keeps the key. This
            // covers in-process failures only: a process crash between reserve and release still
            // leaves the reservation behind, which is what the store's ReservationWindow TTL
            // exists to bound.
            if (ambientTransaction is null &&
                heldReservation is { } releaseScope &&
                heldReservationToken is { } releaseToken &&
                !writeOutcome.MayHaveCommitted)
            {
                await _idempotencyStore.ReleaseAsync(releaseScope, releaseToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates edit operation counts against configured limits
    /// </summary>
    private static IResult? ValidateEditLimits(ApplyEditsRequest request, Honua.Core.Configuration.EditLimits editLimits, HttpContext context)
    {
        var violation = EditLimitPolicy.Evaluate(
            new EditCounts(request.Adds?.Length ?? 0, request.Updates?.Length ?? 0, request.Deletes?.Length ?? 0),
            editLimits);
        return violation switch
        {
            null => null,
            { Kind: EditLimitViolationKind.FeaturesPerOperation } => StandardErrorHelpers.CreateBadRequest(context,
                "Too many features in a single edit operation",
                [$"Maximum per operation: {violation.Value.Limit}"]),
            _ => StandardErrorHelpers.CreateBadRequest(context,
                "Too many edits in a single request",
                [$"Maximum per request: {violation.Value.Limit}"])
        };
    }

    private static int? ResolveStorageLayerIdV2(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource)
        => snapshot.ResolveStorageLayerId(publication, resource);

    /// <summary>
    /// Processes add, update, and delete operations from the request
    /// </summary>
    private async Task<EditOperationContext> ProcessEditOperationsAsync(
        ApplyEditsRequest request,
        MetadataV2Resource resource,
        int storageLayerId,
        EditPrincipal principal,
        EditLockScope? lockScope,
        bool guardUpdateSnapshot,
        VersionContext? versionContext,
        CancellationToken cancellationToken)
    {
        var context = new EditOperationContext
        {
            AddResults = request.Adds is { Length: > 0 } ? new EditResult?[request.Adds.Length] : null,
            UpdateResults = request.Updates is { Length: > 0 } ? new EditResult?[request.Updates.Length] : null,
            DeleteResults = request.Deletes is { Length: > 0 } ? new EditResult?[request.Deletes.Length] : null,
            RequiresOwnerGuards = OwnerEditPolicyFieldAccess.RequiresStoredOwner(resource.OwnerEditPolicy, principal),
            LockScope = lockScope
        };

        await ProcessAddOperationsAsync(request, context, resource, principal, cancellationToken);
        await ProcessUpdateOperationsAsync(
            request,
            context,
            resource,
            storageLayerId,
            principal,
            guardUpdateSnapshot,
            versionContext,
            cancellationToken);
        await ProcessDeleteOperationsAsync(request, resource, storageLayerId, context, principal, versionContext, cancellationToken);

        return context;
    }

    /// <summary>
    /// Resolves the authenticated <see cref="EditPrincipal"/> for owner-based edit policies
    /// from the request's auth context: the principal name, whether the caller is
    /// authenticated, and whether it holds the administrative override role.
    /// </summary>
    private static EditPrincipal ResolveEditPrincipal(HttpContext httpContext)
        => ServiceDataEditorAuthorization.ResolveEditPrincipal(httpContext);

    /// <summary>
    /// Processes add operations and tracks features to create
    /// </summary>
    private async Task ProcessAddOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        MetadataV2Resource resource,
        EditPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (request.Adds == null)
            return;

        for (var i = 0; i < request.Adds.Length; i++)
        {
            try
            {
                // Capture request intent before BuildFeatureFromGeoServicesAsync runs;
                // for adds existingFeature is null so the post-merge geometry equals the
                // request's, but using request.Adds[i].Geometry directly keeps the rule
                // identical to the update path.
                var requestHasGeometry = request.Adds[i].Geometry != null;
                var newFeature = await BuildFeatureFromGeoServicesAsync(
                    request.Adds[i],
                    0,
                    resource,
                    AttributeRuleEditEvent.Insert,
                    principal,
                    cancellationToken,
                    useGlobalIds: request.UseGlobalIds);
                var createRejection = EditProcessor.DescribeCreateAttributeRejection(resource, newFeature.Attributes);
                if (createRejection is not null)
                {
                    throw new ArgumentException(createRejection);
                }

                context.CreateFeatures.Add(newFeature);
                context.CreateIndexes.Add(i);
                context.CreateGeometryChanged.Add(requestHasGeometry);
                context.CreateResponseObjectIds.Add(TryGetObjectId(newFeature.Attributes.ToDictionary(), resource, out var responseObjectId)
                    ? responseObjectId
                    : null);
            }
            catch (EditNotPermittedException ex)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.NotPermitted,
                    description: SanitizeEditErrorMessage(ex.Message, "Edit not permitted."));
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.ValidationFailed,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage));
            }
            // Intentional catch-all: one add-slot's unexpected failure must not abort the whole
            // batch; it is logged and reported as a per-slot GeoServices edit failure.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.FeatureAddFailed(logger, i, ex.Message, ex);
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.GenericFailure,
                    description: "Failed to add feature");
            }
        }
    }

    /// <summary>
    /// Processes update operations and tracks features to update
    /// </summary>
    internal async Task ProcessUpdateOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        MetadataV2Resource resource,
        int storageLayerId,
        EditPrincipal principal,
        bool guardUpdateSnapshot,
        VersionContext? versionContext,
        CancellationToken cancellationToken)
    {
        if (request.Updates == null)
            return;

        // First pass: parse the per-slot object ids so the existing rows can be
        // resolved with a single batched lookup instead of one round-trip per update.
        var slotObjectIds = new long?[request.Updates.Length];
        for (var i = 0; i < request.Updates.Length; i++)
        {
            var hasObjectId = TryGetObjectId(request.Updates[i].Attributes, resource, out var parsedObjectId);
            if (!hasObjectId || parsedObjectId <= 0)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.InvalidObjectId,
                    description: hasObjectId
                        ? "Invalid Object ID for update operation"
                        : "ObjectId is required for update operations",
                    objectId: hasObjectId ? parsedObjectId : null);
                continue;
            }

            slotObjectIds[i] = parsedObjectId;
        }

        IReadOnlyDictionary<long, Feature> existingFeatures;
        try
        {
            existingFeatures = await ResolveFeaturesByGeoServicesObjectIdsAsync(
                resource,
                storageLayerId,
                slotObjectIds,
                versionContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // ObjectId field translation failed; this is schema-level and would have
            // failed every slot under the previous per-feature resolve as well.
            var description = SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage);
            for (var i = 0; i < slotObjectIds.Length; i++)
            {
                if (slotObjectIds[i] is { } failedObjectId)
                {
                    context.HasValidationErrors = true;
                    context.UpdateResults![i] = CreateFailureResult(
                        code: GeoServicesEditErrorCodes.InvalidObjectId,
                        description: description,
                        objectId: failedObjectId);
                }
            }

            return;
        }

        var ownerSnapshots = await ReadOwnerSnapshotsAsync(
            resource,
            storageLayerId,
            existingFeatures.Values,
            principal,
            cancellationToken,
            versionContext).ConfigureAwait(false);

        for (var i = 0; i < request.Updates.Length; i++)
        {
            if (slotObjectIds[i] is not { } objectId)
            {
                continue;
            }

            var update = request.Updates[i];
            try
            {
                Feature? existingFeature = existingFeatures.TryGetValue(objectId, out var resolvedFeature) ? resolvedFeature : null;
                // The pre-read (ResolveFeaturesByGeoServicesObjectIdsAsync) is RLS-enforced on
                // both the fast path and the custom-objectid path, so a null result means the
                // row does not exist OR is hidden from this caller by row-level security. The
                // edit SQL filters only on (layer_id, objectid) with no RLS predicate, so the
                // not-found rejection MUST be unconditional — skipping it on the default-OBJECTID
                // fast path would let a caller mutate an RLS-hidden row (#2066).
                if (existingFeature is null)
                {
                    context.HasValidationErrors = true;
                    context.UpdateResults![i] = CreateFailureResult(
                        code: GeoServicesEditErrorCodes.NotFound,
                        description: "Feature not found",
                        objectId: objectId);
                    continue;
                }

                // existingFeature is guaranteed to have a value here (the null case returns above),
                // so the previous `existingFeature?.Id ?? objectId` had a dead `?? objectId` branch
                // that could never execute; access .Value.Id directly instead.
                // Collaborative-editing lease enforcement (#4402): reject the slot when
                // another editor holds an active lock on this feature. Emitted as the stable
                // per-feature FeatureLocked code so an Esri client can branch on it, and
                // recorded as a validation error so rollbackOnFailure=true fails the whole
                // batch. Either way the stored row is left exactly as the holder left it.
                if (await EvaluateEditLockAsync(context.LockScope, objectId, "update", cancellationToken)
                        .ConfigureAwait(false) is { } updateLockConflict)
                {
                    context.HasValidationErrors = true;
                    context.UpdateResults![i] = CreateFailureResult(
                        code: GeoServicesEditErrorCodes.FeatureLocked,
                        description: updateLockConflict,
                        objectId: objectId);
                    continue;
                }

                var internalObjectId = existingFeature.Value.Id;
                context.InternalObjectIdsByPublicObjectId[objectId] = internalObjectId;
                if (guardUpdateSnapshot)
                {
                    // QueryAsync masks in SQL and does not stamp ReadStateToken. GetAsync
                    // hashes the full row first, then strips masked keys before returning.
                    var precondition = await TryCreateGuardedUpdatePreconditionAsync(
                        _featureReader,
                        storageLayerId,
                        internalObjectId,
                        cancellationToken).ConfigureAwait(false);
                    if (precondition is not { } guarded)
                    {
                        context.HasValidationErrors = true;
                        context.UpdateResults![i] = CreateFailureResult(
                            code: GeoServicesEditErrorCodes.NotFound,
                            description: "Feature not found",
                            objectId: objectId);
                        continue;
                    }

                    context.UpdateSnapshotPreconditions[internalObjectId] = guarded;
                }
                // Capture request intent BEFORE BuildFeatureFromGeoServicesAsync runs;
                // that helper preserves existingFeature.Geometry when update.Geometry is
                // null, so the post-merge feature's WKB cannot distinguish an attribute-
                // only update on a spatial row from a geometry change.
                var requestHasGeometry = update.Geometry != null || update.ClearGeometry;
                var updateFeature = await BuildFeatureFromGeoServicesAsync(
                    update,
                    internalObjectId,
                    resource,
                    AttributeRuleEditEvent.Update,
                    principal,
                    cancellationToken,
                    existingFeature,
                    ownerSnapshots,
                    request.UseGlobalIds).ConfigureAwait(false);
                CaptureOwnerGuard(context, resource, ownerSnapshots, internalObjectId);
                context.UpdateFeatures.Add(updateFeature);
                // The full merged row above serves rules, validation, hooks and events. The
                // DEFAULT writer receives only what this request changes, so the row it locks
                // supplies every other field and the omitted geometry (#6094). The versioned
                // overlay writer persists the payload verbatim and keeps the merged row.
                context.UpdateWriteFeatures.Add(versionContext is { IsDefault: false }
                    ? updateFeature
                    : CreatePartialUpdateWritePayload(updateFeature, existingFeature.Value, update, requestHasGeometry, resource));
                context.UpdateIndexes.Add(i);
                context.UpdateObjectIds.Add(objectId);
                context.UpdateGeometryChanged.Add(requestHasGeometry);
            }
            catch (EditNotPermittedException ex)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.NotPermitted,
                    description: SanitizeEditErrorMessage(ex.Message, "Edit not permitted."),
                    objectId: objectId);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.ValidationFailed,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage),
                    objectId: objectId);
            }
            // Intentional catch-all: one update-slot's unexpected failure must not abort the whole
            // batch; it is logged and reported as a per-slot GeoServices edit failure.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.FeatureUpdateFailed(logger, i, ex.Message, ex);
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.GenericFailure,
                    description: "Failed to update feature",
                    objectId: objectId);
            }
        }
    }

    /// <summary>
    /// Processes delete operations and tracks features to delete
    /// </summary>
    internal async Task ProcessDeleteOperationsAsync(
        ApplyEditsRequest request,
        MetadataV2Resource resource,
        int storageLayerId,
        EditOperationContext context,
        EditPrincipal principal,
        VersionContext? versionContext,
        CancellationToken cancellationToken)
    {
        if (request.Deletes == null)
            return;

        // First pass: parse the per-slot object ids so the existing rows can be
        // resolved with a single batched lookup instead of one round-trip per delete.
        var slotObjectIds = new long?[request.Deletes.Length];
        for (var i = 0; i < request.Deletes.Length; i++)
        {
            if (!FeatureServerValueParser.TryConvertToLong(request.Deletes[i], out var parsedObjectId) ||
                parsedObjectId <= 0)
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.InvalidObjectId,
                    description: "Invalid Object ID for delete operation",
                    objectId: parsedObjectId > 0 ? parsedObjectId : null);
                continue;
            }

            slotObjectIds[i] = parsedObjectId;
        }

        IReadOnlyDictionary<long, Feature> existingFeatures;
        try
        {
            existingFeatures = await ResolveFeaturesByGeoServicesObjectIdsAsync(
                resource,
                storageLayerId,
                slotObjectIds,
                versionContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // ObjectId field translation failed; this is schema-level and would have
            // failed every slot under the previous per-feature resolve as well.
            var description = SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage);
            for (var i = 0; i < slotObjectIds.Length; i++)
            {
                if (slotObjectIds[i] is { } failedObjectId)
                {
                    context.HasValidationErrors = true;
                    context.DeleteResults![i] = CreateFailureResult(
                        code: GeoServicesEditErrorCodes.InvalidObjectId,
                        description: description,
                        objectId: failedObjectId);
                }
            }

            return;
        }

        var expectedAbsentObjectIds = request.Preconditions.IsDefaultOrEmpty
            ? null
            : request.Preconditions
                .Where(static precondition => precondition.ExpectedRowAbsent)
                .Select(static precondition => precondition.ObjectId)
                .ToHashSet();
        var usesInternalObjectIds = ShouldUseInternalObjectIdFastPath(resource);
        var ownerSnapshots = await ReadOwnerSnapshotsAsync(
            resource,
            storageLayerId,
            existingFeatures.Values,
            principal,
            cancellationToken,
            versionContext).ConfigureAwait(false);

        for (var i = 0; i < request.Deletes.Length; i++)
        {
            if (slotObjectIds[i] is not { } objectId)
            {
                continue;
            }

            Feature? existingFeature = existingFeatures.TryGetValue(objectId, out var resolvedFeature) ? resolvedFeature : null;
            // The pre-read is RLS-enforced on both the fast path and the custom-objectid path,
            // so a null result means the row is missing OR hidden from this caller by RLS. The
            // DELETE SQL filters only on (layer_id, objectid) with no RLS predicate, so ordinary
            // requests MUST reject this result — skipping it on the default-OBJECTID fast path would
            // let a caller delete an RLS-hidden row (#2066). The only exception below is a server-only
            // expected-absence precondition, which the transactional writer re-validates (#2430).
            if (existingFeature is null)
            {
                if (expectedAbsentObjectIds?.Contains(objectId) == true && usesInternalObjectIds)
                {
                    // This is a server-only idempotent conflict-resolution delete. Absence is the
                    // precondition, not a validation failure: dispatch the no-op delete so the writer
                    // reserves the logical key and re-checks absence inside its transaction. A
                    // concurrent insert then fails the precondition instead of being deleted after
                    // this pre-read (#2430).
                    context.DeleteIds.Add(objectId);
                    context.DeleteResponseObjectIds.Add(objectId);
                    context.DeleteIndexes.Add(i);
                    context.DeleteFeatures.Add(null);
                    continue;
                }

                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    // A custom public object-id value cannot identify a missing storage row. Treat an
                    // expected-absence resolution as stale instead of falsely finalizing it without
                    // the provider's storage-key guard.
                    code: expectedAbsentObjectIds?.Contains(objectId) == true
                        ? GeoServicesEditErrorCodes.UpdateConflict
                        : GeoServicesEditErrorCodes.DeleteNotFound,
                    description: expectedAbsentObjectIds?.Contains(objectId) == true
                        ? "Feature identity could not be transactionally verified"
                        : "Feature not found",
                    objectId: objectId);
                continue;
            }

            // Owner-based edit policy (#2132, #5426): the batch owner read is not copied
            // onto the masked feature the caller already holds. A non-owner is still refused.
            var deleteSnapshot = SnapshotFor(ownerSnapshots, existingFeature.Value.Id);
            var ownerDecision = OwnerEditPolicyFieldAccess.Decide(
                resource.OwnerEditPolicy,
                AttributeRuleEditEvent.Delete,
                principal,
                deleteSnapshot);
            if (!ownerDecision.IsAllowed)
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.NotPermitted,
                    description: SanitizeEditErrorMessage(ownerDecision.Reason!, "Edit not permitted."),
                    objectId: objectId);
                continue;
            }

            // Collaborative-editing lease enforcement (#4402), same contract as the update
            // path: a feature another editor holds cannot be deleted out from under them.
            if (await EvaluateEditLockAsync(context.LockScope, objectId, "delete", cancellationToken)
                    .ConfigureAwait(false) is { } deleteLockConflict)
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.FeatureLocked,
                    description: deleteLockConflict,
                    objectId: objectId);
                continue;
            }

            var internalObjectId = existingFeature.Value.Id;
            if (!TryCaptureOwnerGuard(context, resource, ownerSnapshots, internalObjectId))
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.NotPermitted,
                    description: "Edit not permitted.",
                    objectId: objectId);
                continue;
            }
            context.InternalObjectIdsByPublicObjectId[objectId] = internalObjectId;
            context.DeleteIds.Add(internalObjectId);
            context.DeleteResponseObjectIds.Add(objectId);
            context.DeleteIndexes.Add(i);
            context.DeleteFeatures.Add(existingFeature);
        }
    }

    /// <summary>
    /// Executes the validated edit operations in the database. <paramref name="writeOutcome"/> is
    /// raised immediately before the write is dispatched to the provider so the caller can tell a
    /// failure that definitely wrote nothing from one that may have committed rows before it
    /// unwound (#3052).
    /// </summary>
    private async Task<FeatureEditResult> ExecuteEdits(
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        ApplyEditsRequest request,
        string serviceId,
        VersionContext? versionContext,
        EditWriteOutcome writeOutcome,
        CancellationToken cancellationToken,
        IFeatureWriterTransaction? writerTransaction = null)
    {
        if (context.CreateFeatures.Count == 0 && context.UpdateFeatures.Count == 0 && context.DeleteIds.Count == 0)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        var editAdapterResult = await _editParameterAdapter.ConvertAsync(
            new GeoServicesEditRequest
            {
                Creates = context.CreateFeatures.ToImmutableArray(),
                Updates = context.UpdateWriteFeatures.ToImmutableArray(),
                Deletes = context.DeleteIds.ToImmutableArray(),
                RollbackOnFailure = request.RollbackOnFailure,
                UseGlobalIds = request.UseGlobalIds
            },
            resource,
            cancellationToken);
        if (!editAdapterResult.IsSuccess || editAdapterResult.EditRequest == null)
        {
            throw new EditBatchValidationException(editAdapterResult.ErrorMessage ?? "Invalid edit request.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, resource);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, resource);
        if (!editValidation.IsValid)
        {
            throw new EditBatchValidationException(editValidation.ErrorMessage ?? "Invalid edit request.");
        }

        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedEdit, resource);

        // The owner read authorizes each request slot before plugins run. Carry the same
        // expected owner into the writer's UPDATE/DELETE statement for only the surviving
        // rows. Re-authorizing the whole batch here would change the per-row failure
        // contract for rollbackOnFailure=false.
        if (context.RequiresOwnerGuards)
        {
            editBatch = editBatch with { OwnerGuards = BuildOwnerGuards(editBatch, context.OwnerGuards) };
        }

        if (context.GuardUpdateSnapshot)
        {
            editBatch = editBatch with
            {
                Preconditions = editBatch.Updates.Select(feature => context.UpdateSnapshotPreconditions[feature.Id]).ToImmutableArray()
            };
        }

        // Server-side optimistic-concurrency preconditions, when the caller supplied them. The writer
        // re-checks each token against the locked row inside the write transaction, so a concurrent
        // edit between the caller's check and this write fails the operation instead of being
        // overwritten (#2430). Absent for ordinary wire requests, which leaves the batch unchanged.
        if (!request.Preconditions.IsDefaultOrEmpty)
        {
            editBatch = editBatch with
            {
                Preconditions = request.Preconditions
                    .Select(precondition => context.InternalObjectIdsByPublicObjectId.TryGetValue(
                        precondition.ObjectId, out var internalObjectId)
                        ? precondition with { ObjectId = internalObjectId }
                        : precondition)
                    .ToImmutableArray()
            };
        }

        // Thread the resolved branch version onto the canonical edit batch. A null/DEFAULT context
        // leaves the byte-identical non-versioned write path unchanged (#1272, ADR-0051).
        if (versionContext is { IsDefault: false })
        {
            editBatch = editBatch with { VersionContext = versionContext };
        }

        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is required for FeatureServer edit dispatch.");
        // Per-row geometry-change semantics: read the request-intent flags captured by
        // ProcessAdd/UpdateOperationsAsync BEFORE BuildFeatureFromGeoServicesAsync merged
        // the request with the existing row. Deriving from editBatch.Updates[i].Geometry
        // would over-report attribute-only updates as geometry changes because that
        // helper preserves the prior WKB when the request omits geometry.
        var perOperationGeometryChanged = BuildPerOperationGeometryChanged(context);
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            httpContext,
            layerId,
            HonuaTelemetry.Protocols.FeatureServer,
            serviceId: serviceId,
            serviceProtocol: HonuaTelemetry.Protocols.FeatureServer,
            // ToSrid() picks LatestWkid when set so the outbox enrichment fallback
            // matches the inline post-commit path on layers like
            // Wkid=102100/LatestWkid=3857.
            layerSrid: resource.ReadSrid(),
            perOperationGeometryChanged: perOperationGeometryChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);

        // From here the outcome is no longer knowable from control flow alone: under the default
        // when rollbackOnFailure=false the writer commits rows independently, so an exception out of
        // ApplyEditsAsync — a cancellation between rows, a transport fault mid-batch — can unwind
        // with rows already committed and no result to inspect. Raise the flag BEFORE dispatching so
        // the handler's finally keeps the idempotency reservation in exactly that case (#3052); the
        // caller lowers it again only when the returned result proves nothing landed.
        // An ambient transaction commits only after every calculate batch succeeds. Raising
        // the flag here would treat a staged batch as durable and pin its idempotency key
        // across a later rollback (#6359).
        if (writerTransaction is null)
        {
            writeOutcome.MayHaveCommitted = true;
        }

        FeatureEditResult editResult;
        try
        {
            editResult = writerTransaction is null
                ? await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false)
                : await writerTransaction.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (editBatch.RollbackOnFailure && cancellationToken.IsCancellationRequested)
        {
            // rollbackOnFailure is the canonical all-or-nothing provider contract. The caller
            // cancelled before a result was returned, and transactional providers either never
            // reached COMMIT or roll the open transaction back during unwind. Commit-time failures
            // are returned explicitly as an unknown outcome, so this cancellation proves no row
            // committed and the request may release its idempotency reservation.
            writeOutcome.MayHaveCommitted = false;
            throw;
        }
        catch (ReadOnlyFeatureWriteException)
        {
            // This exception is safe to classify only at the provider dispatch boundary. Catching
            // it at the outer request boundary would also match a post-commit invalidator or plugin
            // hook and could release a reservation after rows were already committed.
            writeOutcome.MayHaveCommitted = false;
            throw;
        }

        ApplyResults(context.AddResults, context.CreateIndexes, editResult.CreateResults, FeatureEditOperationKind.Create);
        ApplyCreateResponseObjectIds(context);
        CaptureCreateEventObjectIds(context, editResult.CreateResults);
        ApplyResults(context.UpdateResults, context.UpdateIndexes, editResult.UpdateResults, FeatureEditOperationKind.Update, context.UpdateObjectIds);
        ApplyResults(context.DeleteResults, context.DeleteIndexes, editResult.DeleteResults, FeatureEditOperationKind.Delete, context.DeleteResponseObjectIds);

        return editResult;
    }

    /// <summary>
    /// Build per-operation-kind queues of geometry-change flags from the request-intent
    /// flags captured during request parsing (before merging with existing rows). The
    /// queues match the order ApplyEditsAsync iterates rows for each kind so each
    /// outbox row's <c>GeometryChanged</c> tracks the originating request's intent
    /// rather than the post-merge feature's WKB. Deletes default to false (the inline
    /// publish path also defaults to false for delete events).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<bool>>? BuildPerOperationGeometryChanged(EditOperationContext context)
    {
        if (context.CreateGeometryChanged.Count == 0
            && context.UpdateGeometryChanged.Count == 0
            && context.DeleteIds.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
        if (context.CreateGeometryChanged.Count > 0)
        {
            result["create"] = context.CreateGeometryChanged.ToImmutableArray();
        }
        if (context.UpdateGeometryChanged.Count > 0)
        {
            result["update"] = context.UpdateGeometryChanged.ToImmutableArray();
        }
        if (context.DeleteIds.Count > 0)
        {
            result["delete"] = Enumerable.Repeat(false, context.DeleteIds.Count).ToImmutableArray();
        }
        return result;
    }

    /// <summary>
    /// Creates a rollback response when validation errors occur
    /// </summary>
    private IResult CreateRollbackResponse(EditOperationContext context, string serviceId, int layerId)
    {
        var rollbackError = new EditError
        {
            Code = GeoServicesEditErrorCodes.OperationRolledBack,
            Description = "Operation rolled back due to validation failure"
        };

        ApplyRollbackResults(context.AddResults, context.CreateIndexes, null, rollbackError);
        ApplyRollbackResults(context.UpdateResults, context.UpdateIndexes, context.UpdateObjectIds, rollbackError);
        ApplyRollbackResults(context.DeleteResults, context.DeleteIndexes, context.DeleteResponseObjectIds, rollbackError);

        var response = new ApplyEditsResponse
        {
            AddResults = FinalizeResults(context.AddResults) ?? [],
            UpdateResults = FinalizeResults(context.UpdateResults) ?? [],
            DeleteResults = FinalizeResults(context.DeleteResults) ?? [],
            Success = false
        };

        FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, false);

        return Results.Json(response, FeatureServerJsonContext.Default.ApplyEditsResponse,
            contentType: "application/json");
    }

    /// <summary>
    /// Builds the final response after all operations complete. Returns the response object (rather than
    /// an <see cref="IResult"/>) so the caller can record it in the idempotency store (#2250) before
    /// serializing it.
    /// </summary>
    private static ApplyEditsResponse BuildFinalResponse(EditOperationContext context, FeatureEditResult editResult)
    {
        var finalAddResults = FinalizeResults(context.AddResults) ?? [];
        var finalUpdateResults = FinalizeResults(context.UpdateResults) ?? [];
        var finalDeleteResults = FinalizeResults(context.DeleteResults) ?? [];
        var allSuccess = AreAllResultsSuccessful(finalAddResults) &&
                         AreAllResultsSuccessful(finalUpdateResults) &&
                         AreAllResultsSuccessful(finalDeleteResults) &&
                         !editResult.WasRolledBack &&
                         !context.HasValidationErrors;

        return new ApplyEditsResponse
        {
            AddResults = finalAddResults,
            UpdateResults = finalUpdateResults,
            DeleteResults = finalDeleteResults,
            Success = allSuccess
        };
    }

    private async Task PublishFeatureChangeEventsAsync(
        string serviceId,
        int layerId,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        var requestId = _httpContextAccessor.HttpContext?.TraceIdentifier ?? "unknown";

        for (var i = 0; i < context.CreateFeatures.Count; i++)
        {
            var resultIndex = context.CreateIndexes[i];
            var result = context.AddResults?[resultIndex];
            if (result is not { Success: true, ObjectId: { } objectId })
            {
                continue;
            }

            var eventObjectId = context.CreateEventObjectIds.Count > i
                ? context.CreateEventObjectIds[i] ?? objectId
                : objectId;
            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                eventObjectId,
                "create",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: context.CreateFeatures[i],
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }

        for (var i = 0; i < context.UpdateFeatures.Count; i++)
        {
            var resultIndex = context.UpdateIndexes[i];
            var result = context.UpdateResults?[resultIndex];
            if (result is not { Success: true })
            {
                continue;
            }

            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                context.UpdateFeatures[i].Id,
                "update",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: context.UpdateFeatures[i],
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }

        for (var i = 0; i < context.DeleteIndexes.Count; i++)
        {
            var resultIndex = context.DeleteIndexes[i];
            var result = context.DeleteResults?[resultIndex];
            if (result is not { Success: true })
            {
                continue;
            }

            var deleteFeature = context.DeleteFeatures.Count > i ? context.DeleteFeatures[i] : null;
            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                context.DeleteIds[i],
                "delete",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: deleteFeature,
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the Enterprise plugin edit pipeline (per-feature validators + batch before-hooks)
    /// over the resolved features and applies any rejections back onto the edit context: each
    /// rejected feature is removed from the write set and a failure result is written into its
    /// response slot. A no-op when no plugins are licensed/registered.
    /// </summary>
    private async Task ApplyPluginEditPipelineAsync(
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        if (!_pluginPipeline.HasPlugins)
        {
            return;
        }

        var hookContext = BuildEditHookContext(serviceId, layerId, resource, context);
        if (hookContext.Features.IsDefaultOrEmpty)
        {
            return;
        }

        var outcome = await _pluginPipeline.ValidateAndRunBeforeHooksAsync(hookContext, cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.HasRejections)
        {
            return;
        }

        foreach (var rejection in outcome.Rejections)
        {
            context.HasValidationErrors = true;
            var message = SanitizeEditErrorMessage(rejection.Message, InvalidFeatureDataMessage);
            switch (rejection.Kind)
            {
                case EditKind.Create:
                    RemoveCreateForRejection(context, rejection, message);
                    break;
                case EditKind.Update:
                    RemoveUpdateForRejection(context, rejection, message);
                    break;
                case EditKind.Delete:
                    RemoveDeleteForRejection(context, rejection, message);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs the Enterprise plugin after-edit hooks over the committed features. Best-effort:
    /// the pipeline swallows hook exceptions so a post-write plugin failure cannot affect the
    /// already-committed edit. A no-op when no plugins are licensed/registered.
    /// </summary>
    private async Task RunPluginAfterHooksAsync(
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        if (!_pluginPipeline.HasPlugins)
        {
            return;
        }

        // Only surface rows that were actually committed. With rollbackOnFailure=false a batch can
        // partially fail (ApplyEditsAsync records per-row failures but leaves the create/update/
        // delete lists intact), and after-hooks must not emit downstream side effects for features
        // that failed to write.
        var hookContext = BuildEditHookContext(serviceId, layerId, resource, context, committedOnly: true);
        if (hookContext.Features.IsDefaultOrEmpty)
        {
            return;
        }

        await _pluginPipeline.RunAfterHooksAsync(hookContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the create/update/delete operations in <paramref name="context"/> into a
    /// protocol-neutral <see cref="EditHookContext"/> for the plugin pipeline. Each item carries the
    /// originating request slot so per-feature rejections map back precisely. When
    /// <paramref name="committedOnly"/> is <see langword="true"/> (the after-hook path), only rows
    /// whose response slot reports success are included, so post-write hooks never observe features
    /// that failed to write under a partial-failure (explicit rollbackOnFailure=false) edit.
    /// </summary>
    private EditHookContext BuildEditHookContext(
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        bool committedOnly = false)
    {
        var features = ImmutableArray.CreateBuilder<EditHookFeature>();

        for (var k = 0; k < context.CreateFeatures.Count; k++)
        {
            var requestIndex = context.CreateIndexes[k];
            if (committedOnly && !IsSlotCommitted(context.AddResults, requestIndex))
            {
                continue;
            }

            features.Add(new EditHookFeature(EditKind.Create, requestIndex, ObjectId: null, context.CreateFeatures[k]));
        }

        for (var k = 0; k < context.UpdateFeatures.Count; k++)
        {
            var requestIndex = context.UpdateIndexes[k];
            if (committedOnly && !IsSlotCommitted(context.UpdateResults, requestIndex))
            {
                continue;
            }

            features.Add(new EditHookFeature(EditKind.Update, requestIndex, context.UpdateObjectIds[k], context.UpdateFeatures[k]));
        }

        for (var k = 0; k < context.DeleteIndexes.Count; k++)
        {
            var requestIndex = context.DeleteIndexes[k];
            if (committedOnly && !IsSlotCommitted(context.DeleteResults, requestIndex))
            {
                continue;
            }

            var objectId = context.DeleteResponseObjectIds[k];
            var snapshot = k < context.DeleteFeatures.Count ? context.DeleteFeatures[k] : null;
            features.Add(new EditHookFeature(EditKind.Delete, requestIndex, objectId, snapshot ?? Feature.Create(objectId, geometry: null)));
        }

        var httpContext = _httpContextAccessor.HttpContext;
        return new EditHookContext(
            serviceId,
            layerId,
            resource.Metadata.Name,
            httpContext?.User?.Identity?.Name,
            httpContext?.TraceIdentifier,
            features.ToImmutable());
    }

    private static bool IsSlotCommitted(EditResult?[]? results, int requestIndex)
        => results is not null
           && requestIndex >= 0
           && requestIndex < results.Length
           && results[requestIndex] is { Success: true };

    private static void RemoveCreateForRejection(EditOperationContext context, PluginEditRejection rejection, string message)
    {
        if (context.AddResults != null && rejection.RequestIndex >= 0 && rejection.RequestIndex < context.AddResults.Length)
        {
            context.AddResults[rejection.RequestIndex] = CreateFailureResult(rejection.ErrorCode, message, rejection.ObjectId);
        }

        var k = context.CreateIndexes.IndexOf(rejection.RequestIndex);
        if (k < 0)
        {
            return;
        }

        context.CreateFeatures.RemoveAt(k);
        context.CreateIndexes.RemoveAt(k);
        if (k < context.CreateResponseObjectIds.Count)
        {
            context.CreateResponseObjectIds.RemoveAt(k);
        }

        if (k < context.CreateGeometryChanged.Count)
        {
            context.CreateGeometryChanged.RemoveAt(k);
        }
    }

    private static void RemoveUpdateForRejection(EditOperationContext context, PluginEditRejection rejection, string message)
    {
        if (context.UpdateResults != null && rejection.RequestIndex >= 0 && rejection.RequestIndex < context.UpdateResults.Length)
        {
            context.UpdateResults[rejection.RequestIndex] = CreateFailureResult(rejection.ErrorCode, message, rejection.ObjectId);
        }

        var k = context.UpdateIndexes.IndexOf(rejection.RequestIndex);
        if (k < 0)
        {
            return;
        }

        context.UpdateFeatures.RemoveAt(k);
        if (k < context.UpdateWriteFeatures.Count)
        {
            context.UpdateWriteFeatures.RemoveAt(k);
        }

        context.UpdateIndexes.RemoveAt(k);
        if (k < context.UpdateObjectIds.Count)
        {
            context.UpdateObjectIds.RemoveAt(k);
        }

        if (k < context.UpdateGeometryChanged.Count)
        {
            context.UpdateGeometryChanged.RemoveAt(k);
        }
    }

    private static void RemoveDeleteForRejection(EditOperationContext context, PluginEditRejection rejection, string message)
    {
        if (context.DeleteResults != null && rejection.RequestIndex >= 0 && rejection.RequestIndex < context.DeleteResults.Length)
        {
            context.DeleteResults[rejection.RequestIndex] = CreateFailureResult(rejection.ErrorCode, message, rejection.ObjectId);
        }

        var k = context.DeleteIndexes.IndexOf(rejection.RequestIndex);
        if (k < 0)
        {
            return;
        }

        context.DeleteIds.RemoveAt(k);
        context.DeleteIndexes.RemoveAt(k);
        if (k < context.DeleteResponseObjectIds.Count)
        {
            context.DeleteResponseObjectIds.RemoveAt(k);
        }

        if (k < context.DeleteFeatures.Count)
        {
            context.DeleteFeatures.RemoveAt(k);
        }
    }

    /// <summary>
    /// Carries the "may this request have put rows in the database?" signal out of
    /// <see cref="ExecuteEdits"/> while the write is still in flight (#3052).
    ///
    /// A return value cannot express this: with explicit <c>rollbackOnFailure=false</c> the
    /// writer commits rows independently, so an exception out of the provider unwinds without a
    /// result even though earlier rows are already committed. A mutable holder raised before the
    /// dispatch is observable from the handler's <c>finally</c> on exactly those paths, and decides
    /// whether the idempotency reservation is released or kept.
    /// </summary>
    private sealed class EditWriteOutcome
    {
        /// <summary>
        /// <see langword="true"/> from the moment a write is dispatched to the provider until
        /// something proves nothing was committed — a provider result with no successful or
        /// ambiguous operations, or a read-only rejection at dispatch. While it is
        /// <see langword="true"/> the handler keeps the idempotency reservation rather than freeing
        /// a key whose rows may exist.
        /// </summary>
        public bool MayHaveCommitted { get; set; }
    }

    /// <summary>
    /// Context object to track edit operations state
    /// </summary>
    internal sealed class EditOperationContext
    {
        public bool RequiresOwnerGuards { get; init; }
        public Dictionary<long, OwnerEditGuard> OwnerGuards { get; } = new();
        public EditResult?[]? AddResults { get; init; }
        public EditResult?[]? UpdateResults { get; init; }
        public EditResult?[]? DeleteResults { get; init; }
        public List<Feature> CreateFeatures { get; } = new();
        public List<int> CreateIndexes { get; } = new();
        public List<long?> CreateResponseObjectIds { get; } = new();
        public List<long?> CreateEventObjectIds { get; } = new();
        /// <summary>
        /// Per-create flag: true when the originating request body included a Geometry;
        /// captured before merging so the outbox payload's GeometryChanged tracks the
        /// request's intent rather than the post-merge feature's WKB.
        /// </summary>
        public List<bool> CreateGeometryChanged { get; } = new();
        public List<Feature> UpdateFeatures { get; } = new();
        /// <summary>
        /// Per-update payload sent to the writer, parallel to <see cref="UpdateFeatures"/>. A
        /// partial update carries only the attributes this request changes and defers omitted
        /// fields and geometry to the writer's locked read (#6094).
        /// </summary>
        public List<Feature> UpdateWriteFeatures { get; } = new();
        public bool GuardUpdateSnapshot { get; set; }
        public Dictionary<long, FeatureEditPrecondition> UpdateSnapshotPreconditions { get; } = new();
        public List<int> UpdateIndexes { get; } = new();
        public List<long> UpdateObjectIds { get; } = new();
        /// <summary>
        /// Per-update flag: true when the originating request body included a Geometry;
        /// captured before <c>BuildFeatureFromGeoServicesAsync</c> merges with the existing
        /// row, since BuildFeatureFromGeoServicesAsync preserves <c>existingFeature.Geometry</c>
        /// when the request omits geometry, otherwise an attribute-only update on a spatial
        /// row would be reported as a geometry change.
        /// </summary>
        public List<bool> UpdateGeometryChanged { get; } = new();
        public List<long> DeleteIds { get; } = new();
        public Dictionary<long, long> InternalObjectIdsByPublicObjectId { get; } = new();
        public List<long> DeleteResponseObjectIds { get; } = new();
        public List<Feature?> DeleteFeatures { get; } = new();
        public List<int> DeleteIndexes { get; } = new();
        public bool HasValidationErrors { get; set; }

        /// <summary>
        /// Collaborative-editing lock scope for this request, or <see langword="null"/>
        /// when no lease is held anywhere and per-feature evaluation is unnecessary (#4402).
        /// </summary>
        public EditLockScope? LockScope { get; init; }
    }

    /// <summary>
    /// The collaborative-editing lock context for one applyEdits request: which
    /// service/layer the targeted features belong to in the lease namespace, and the
    /// holder identity the caller is editing under (#4402).
    /// </summary>
    internal sealed record EditLockScope(string ServiceName, int LayerId, LockHolder? Holder);

    /// <summary>
    /// Builds the lock scope for a request, or returns <see langword="null"/> when the
    /// lease store holds nothing at all so the batch can skip guard evaluation entirely.
    /// </summary>
    private async Task<EditLockScope?> ResolveEditLockScopeAsync(
        HttpContext httpContext,
        MetadataV2Service service,
        MetadataV2Publication publication,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (!await FeatureEditLockEnforcement.IsEvaluationRequiredAsync(_featureLocks, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The lease namespace is (service name, protocol layer id, protocol object id):
        // exactly the triple a GeoServices client used to claim the lease, and the same
        // triple the OGC API Features and OData write paths resolve to for the same row.
        var serviceName = FeatureEditLockEnforcement.ResolveServiceName(service, publication);
        return string.IsNullOrWhiteSpace(serviceName)
            ? null
            : new EditLockScope(
                serviceName,
                FeatureEditLockEnforcement.ResolveLayerId(publication, layerId),
                FeatureEditLockEnforcement.ResolveHolder(httpContext));
    }

    /// <summary>
    /// Evaluates one feature mutation against the active leases, returning the
    /// client-facing description when the edit is blocked by another editor's lease.
    /// </summary>
    private async Task<string?> EvaluateEditLockAsync(
        EditLockScope? scope,
        long objectId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (scope is null)
        {
            return null;
        }

        var conflict = await FeatureEditLockEnforcement.EvaluateAsync(
            _editGuard,
            scope.ServiceName,
            scope.LayerId,
            objectId,
            operation,
            scope.Holder,
            cancellationToken).ConfigureAwait(false);

        return conflict is null ? null : FeatureEditLockEnforcement.Describe(conflict);
    }

    /// <summary>
    /// Resolves the existing rows for a batch of GeoServices object ids with a single
    /// provider round-trip (internal-objectid fast path uses <see cref="FeatureQuery.ObjectIds"/>;
    /// custom objectid fields use one translated IN filter), keyed by the GeoServices
    /// object id. Missing rows are simply absent from the result.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, Feature>> ResolveFeaturesByGeoServicesObjectIdsAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        long?[] slotObjectIds,
        VersionContext? versionContext,
        CancellationToken cancellationToken)
    {
        var objectIds = new List<long>(slotObjectIds.Length);
        var seen = new HashSet<long>();
        // OfType<long> both drops null slots and unwraps the Nullable<long>; seen.Add doubles as
        // the de-duplication predicate (HashSet<T>.Add returns false for an already-seen value).
        objectIds.AddRange(slotObjectIds.OfType<long>().Where(seen.Add));

        var resolved = new Dictionary<long, Feature>(objectIds.Count);
        if (objectIds.Count == 0)
        {
            return resolved;
        }

        if (ShouldUseInternalObjectIdFastPath(resource))
        {
            var fastPathResult = await _featureReader.QueryAsync(
                storageLayerId,
                new FeatureQuery
                {
                    ObjectIds = objectIds.ToImmutableArray(),
                    Limit = objectIds.Count,
                    VersionContext = versionContext is { IsDefault: false } ? versionContext : null
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var feature in fastPathResult.Items)
            {
                resolved[feature.Id] = feature;
            }

            return resolved;
        }

        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null)
        {
            return resolved;
        }

        var expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.In,
            new ValueList(objectIds
                .Select(static objectId => new Literal(objectId, LiteralType.Number))
                .ToArray()));
        var translation = _filterExpressionService.Translate(expression, resource);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid ObjectId field.");
        }

        var result = await _featureReader.QueryAsync(
            storageLayerId,
            new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = objectIds.Count,
                VersionContext = versionContext is { IsDefault: false } ? versionContext : null
            },
            cancellationToken).ConfigureAwait(false);

        var featureIndex = 0;
        while (featureIndex < result.Items.Length)
        {
            var feature = result.Items[featureIndex];
            if (feature.Attributes.TryGetValue(objectIdField.Name, out var rawValue) &&
                FeatureServerValueParser.TryConvertToLong(rawValue, out var objectId))
            {
                resolved[objectId] = feature;
            }

            featureIndex++;
        }

        return resolved;
    }

    private static bool ShouldUseInternalObjectIdFastPath(MetadataV2Resource resource)
        => GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource)?.Name.Equals(
            FieldNames.ObjectId,
            StringComparison.OrdinalIgnoreCase) != false;

    internal async Task<Feature> BuildFeatureFromGeoServicesAsync(
        GeoServicesFeature feature,
        long objectId,
        MetadataV2Resource resource,
        AttributeRuleEditEvent editEvent,
        EditPrincipal principal,
        CancellationToken cancellationToken,
        Feature? existingFeature = null,
        IReadOnlyDictionary<long, OwnerFieldSnapshot>? ownerSnapshots = null,
        bool useGlobalIds = false,
        TimeProvider? timeProvider = null)
    {
        byte[]? geometry = existingFeature?.Geometry;
        if (feature.ClearGeometry)
        {
            geometry = null;
        }

        if (feature.Geometry != null)
        {
            // Enforce the layer's declared geometry type. ArcGIS rejects a feature whose
            // geometry shape does not match the layer's geometryType (e.g. a polygon sent to
            // an esriGeometryPoint layer). Without this the WKB converter would happily store
            // the mismatched shape, silently corrupting the layer's geometry homogeneity.
            var layerGeometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
            if (!IsGeometryTypeCompatible(feature.Geometry, layerGeometryType, out var geometryTypeError))
            {
                throw new ArgumentException(geometryTypeError);
            }

            // Layer 1: Validate Esri JSON input
            var esriValidation = _geometryServices.ValidateEsriJson(feature.Geometry);
            if (!esriValidation.IsValid)
            {
                var errorMessages = string.Join("; ", esriValidation.Errors.Select(e => e.Message));
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {errorMessages}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            var layerSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
            var geometrySrid = feature.Geometry.SpatialReference?.Wkid
                ?? feature.Geometry.SpatialReference?.LatestWkid;
            // Treat Web Mercator aliases as equivalent. ArcGIS Pro / the ArcGIS JS API send
            // {wkid:102100, latestWkid:3857} against a layer stored as 3857; these are the same
            // CRS with identical coordinate values. Normalize both sides through the shared
            // Web Mercator alias table (the same normalization the query and import paths use)
            // before comparing so the edit is accepted. A genuinely different CRS still fails
            // here because the edit path does not reproject geometry.
            if (geometrySrid.HasValue &&
                SpatialReferenceExtensions.NormalizeWebMercatorSrid(geometrySrid.Value)
                    != SpatialReferenceExtensions.NormalizeWebMercatorSrid(layerSrid))
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry spatial reference {geometrySrid.Value} does not match layer SRID {layerSrid}.",
                    "Geometry spatial reference does not match layer SRID.");
                throw new ArgumentException(safeError);
            }

            // The incoming spatial reference either matches the layer or is a Web Mercator
            // alias of it, so store the geometry tagged with the layer's canonical SRID.
            try
            {
                geometry = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(
                    feature.Geometry,
                    layerSrid,
                    GeoServicesGeometryConverter.ResolveConfiguredEditCurveVertexBudget(
                        _httpContextAccessor.HttpContext?.RequestServices));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    SanitizeEditErrorMessage(ex.Message, InvalidGeometryPayloadMessage),
                    ex);
            }

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(resource, geometry, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {geometryValidation.ErrorMessage}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            geometry = geometryValidation.Geometry;
        }

        IReadOnlyDictionary<string, object?> attributesToValidate = feature.Attributes;
        MetadataV2Field[] nonEditableFields = [];
        if (existingFeature is not null &&
            (feature.ReplaceAttributes || feature.InheritedNonEditableAttributes.Count > 0))
        {
            // Captured conflict envelopes contain the row's read-only fields as well as its editable
            // business state. Inherited values are not an operator mutation: validate only explicitly
            // selected/editable fields and keep read-only values from the currently addressed row. For
            // complete-state replacement this also keeps a custom non-editable id.primary available
            // after rebuilding the attribute bag.
            nonEditableFields = resource.SchemaFields.Where(field => !field.Editable).ToArray();
            var nonEditableNames = feature.ReplaceAttributes
                ? nonEditableFields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : feature.InheritedNonEditableAttributes;
            attributesToValidate = feature.Attributes
                .Where(entry => !nonEditableNames.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        if (existingFeature is not null)
        {
            // The update's object-ID attribute addressed the row during the pre-read.
            // It is not a mutation of that row's identity, including when a custom
            // primary field is correctly advertised as non-editable.
            attributesToValidate = attributesToValidate
                .Where(entry => !entry.Key.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        }

        // Drop client GlobalID, editor-tracking, and (on insert) other non-editable
        // values before validation. Stamp replaces them after rules run (#6134).
        attributesToValidate = ServerMaintainedEditFields.OmitClientValues(
            resource,
            attributesToValidate,
            isUpdate: existingFeature is not null,
            useGlobalIds);

        var attributesResult = _mutationValidator.ValidateAttributes(
            resource,
            attributesToValidate,
            ValidationExtensions.AttributeValidationMode.GeoServices,
            isUpdate: existingFeature is not null);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(
                SanitizeEditErrorMessage(attributesResult.ErrorMessage, "Invalid attributes."));
        }

        var attributes = existingFeature is not null && !feature.ReplaceAttributes
            ? existingFeature.Value.Attributes.ToBuilder()
            : ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (existingFeature is not null && feature.ReplaceAttributes)
        {
            var currentAttributes = existingFeature.Value.Attributes;
            foreach (var field in nonEditableFields.Where(field =>
                         currentAttributes.ContainsKey(field.Name)))
            {
                attributes[field.Name] = currentAttributes[field.Name];
            }
        }
        foreach (var (key, value) in attributesResult.Value!)
        {
            attributes[key] = value;
        }

        if (objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            attributes.Remove(objectIdFieldName);
        }
        else if (existingFeature is { } currentFeature &&
                 currentFeature.Attributes.TryGetValue(objectIdFieldName, out var currentObjectId))
        {
            attributes[objectIdFieldName] = currentObjectId;
        }

        // Esri attribute rules fire on the shared edit path after attribute validation
        // and merge: calculation rules populate their target field, constraint/validation
        // rules reject violating edits. Expressions outside the supported safe subset are
        // routed out of scope (skipped with a logged warning) by the engine, keeping full
        // Arcade parity a non-goal of this path. Throwing ArgumentException lets the
        // surrounding per-feature try/catch convert a violation into a clean edit failure
        // result rather than a 500.
        var ruleResult = AttributeRuleEngine.Apply(
            resource,
            attributes,
            editEvent,
            new UnsupportedExpressionLogger(_logger));
        if (!ruleResult.IsValid)
        {
            var message = ruleResult.Violations[0].Message;
            throw new ArgumentException(SanitizeEditErrorMessage(message, "Attribute rule violation."));
        }

        if (!ReferenceEquals(ruleResult.Attributes, (IReadOnlyDictionary<string, object?>)attributes))
        {
            attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in ruleResult.Attributes)
            {
                attributes[key] = value;
            }
        }

        // Owner-based edit policy (#2132, #5426): authorize update against the batch's
        // unmasked owner read and stamp the owner on insert. That read is not copied into
        // attributes. When the owner was masked, the writer keeps the stored value via
        // PreserveOmittedMaskedAttributes, so the response still omits it.
        if (resource.OwnerEditPolicy is { Enabled: true } ownerPolicy)
        {
            var updateSnapshot = editEvent == AttributeRuleEditEvent.Insert || existingFeature is not { } existing
                ? null
                : SnapshotFor(ownerSnapshots, existing.Id);
            var ownerDecision = OwnerEditPolicyFieldAccess.Decide(
                ownerPolicy,
                editEvent,
                principal,
                updateSnapshot);
            if (!ownerDecision.IsAllowed)
            {
                throw new EditNotPermittedException(SanitizeEditErrorMessage(ownerDecision.Reason!, "Edit not permitted."));
            }

            if (editEvent == AttributeRuleEditEvent.Insert &&
                OwnerEditPolicyEvaluator.ShouldStampOwnerOnInsert(ownerPolicy))
            {
                attributes[ownerPolicy.OwnerField] = principal.Name;
            }
            else if (editEvent != AttributeRuleEditEvent.Insert && !principal.IsAdmin)
            {
                var ownerAttribute = attributes.FirstOrDefault(pair =>
                    pair.Key.Equals(ownerPolicy.OwnerField, StringComparison.OrdinalIgnoreCase));
                if (OwnerEditPolicyFieldAccess.RejectsOwnerReassignment(
                        ownerFieldPresent: ownerAttribute.Key is not null,
                        replacesAllAttributes: feature.ReplaceAttributes,
                        submittedOwner: ownerAttribute.Value,
                        storedOwner: updateSnapshot?.Owner))
                {
                    // Reject reassignment without turning a full replacement into a partial update
                    // or copying the unmasked owner snapshot into the response attribute bag.
                    throw new EditNotPermittedException(OwnerEditPolicyFieldAccess.OwnerReassignmentDeniedReason);
                }
            }
        }

        var clock = timeProvider
            ?? _httpContextAccessor.HttpContext?.RequestServices.GetService<TimeProvider>()
            ?? TimeProvider.System;
        ServerMaintainedEditFields.Stamp(
            resource,
            attributes,
            isInsert: editEvent == AttributeRuleEditEvent.Insert,
            principal,
            clock.GetUtcNow(),
            useGlobalIds,
            existingFeature?.Attributes);

        // Contingent-value enforcement (#2133): the effective merged attribute row (existing
        // values + this edit) must satisfy the resource's restrictive contingent-value groups.
        // An invalid cross-field combination is rejected per-feature naming the offending group.
        var contingentResult = ContingentValueValidator.Validate(resource, attributes);
        if (!contingentResult.IsValid)
        {
            throw new ArgumentException(
                SanitizeEditErrorMessage(contingentResult.Violations[0].Message, "Invalid attribute combination."));
        }

        var edited = Feature.Create(objectId, geometry, attributes.ToImmutable());
        var isPartialUpdate = existingFeature is not null && !feature.ReplaceAttributes;
        edited = MarkPartialGeoServicesUpdate(edited, isPartialUpdate);
        if (!edited.PreserveOmittedMaskedAttributes &&
            OwnerEditPolicyFieldAccess.ShouldPreserveStoredOwner(
                resource.OwnerEditPolicy,
                isPartialUpdate,
                attributes.Keys))
        {
            edited = edited with { PreserveOmittedMaskedAttributes = true };
        }

        return edited;
    }

    /// <summary>
    /// Marks a partial update so the writer keeps omitted attributes, including masked ones.
    /// A full attribute replacement leaves the flag unset.
    /// </summary>
    internal static Feature MarkPartialGeoServicesUpdate(Feature edited, bool isPartialUpdate)
        => isPartialUpdate
            ? edited with { PreserveOmittedMaskedAttributes = true }
            : edited;

    /// <summary>
    /// Builds the writer payload for a partial GeoServices update (#6094). The merged feature
    /// starts from an unlocked pre-read, so writing it whole would revert any field or geometry
    /// another editor committed after that read. The payload keeps the caller-supplied
    /// attributes plus every value this request changed relative to the pre-read (attribute
    /// rule outputs, owner handling), names attributes the request removed, and asks the
    /// writer to take omitted geometry from the row it locks. Full replacements and updates
    /// that do not preserve omitted attributes are returned unchanged.
    /// </summary>
    internal static Feature CreatePartialUpdateWritePayload(
        Feature merged,
        Feature snapshot,
        GeoServicesFeature request,
        bool requestHasGeometry,
        MetadataV2Resource resource)
    {
        if (request.ReplaceAttributes || !merged.PreserveOmittedMaskedAttributes)
        {
            return merged;
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Attributes.Keys)
        {
            if (!request.InheritedNonEditableAttributes.Contains(key) &&
                !key.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                supplied.Add(key);
            }
        }

        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in merged.Attributes)
        {
            if (supplied.Contains(name) ||
                !snapshot.Attributes.TryGetValue(name, out var previous) ||
                !Equals(previous, value))
            {
                attributes[name] = value;
            }
        }

        var removals = merged.ExplicitAttributeRemovals.IsDefault
            ? ImmutableArray.CreateBuilder<string>()
            : merged.ExplicitAttributeRemovals.ToBuilder();
        foreach (var name in snapshot.Attributes.Keys)
        {
            if (!merged.Attributes.ContainsKey(name) &&
                !name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                removals.Add(name);
            }
        }

        return merged with
        {
            Attributes = attributes.ToImmutable(),
            ExplicitAttributeRemovals = removals.ToImmutable(),
            PreserveOmittedGeometry = !requestHasGeometry
        };
    }

    /// <summary>
    /// Loads the single-update edit snapshot and returns its full-row state token.
    /// Returns null when the row is gone. Does not attach a precondition for multi-row batches.
    /// </summary>
    internal static async Task<FeatureEditPrecondition?> TryCreateGuardedUpdatePreconditionAsync(
        IFeatureReader reader,
        int storageLayerId,
        long storageFeatureId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (await reader.GetAsync(storageLayerId, storageFeatureId, cancellationToken).ConfigureAwait(false)
            is not { } editSnapshot)
        {
            return null;
        }

        return new FeatureEditPrecondition
        {
            ObjectId = editSnapshot.Id,
            ExpectedStateToken = FeatureStateToken.FromReadSnapshot(editSnapshot)
        };
    }

    /// <summary>
    /// Loads owner values for a whole update or delete batch. Returns null when this caller
    /// does not need the stored owner, so inserts and administrators do not query.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, OwnerFieldSnapshot>?> ReadOwnerSnapshotsAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        IEnumerable<Feature> existingFeatures,
        EditPrincipal principal,
        CancellationToken cancellationToken,
        VersionContext? versionContext = null)
    {
        if (!OwnerEditPolicyFieldAccess.RequiresStoredOwner(resource.OwnerEditPolicy, principal))
        {
            return null;
        }

        var ids = new List<long>();
        foreach (var feature in existingFeatures)
        {
            ids.Add(feature.Id);
        }

        return await OwnerEditPolicyFieldAccess.ReadOwnersAsync(
            _featureReader,
            storageLayerId,
            ids,
            resource.OwnerEditPolicy!.OwnerField,
            versionContext,
            cancellationToken).ConfigureAwait(false);
    }

    private static OwnerFieldSnapshot? SnapshotFor(
        IReadOnlyDictionary<long, OwnerFieldSnapshot>? snapshots,
        long storageFeatureId)
    {
        if (snapshots is null)
        {
            return null;
        }

        return snapshots.TryGetValue(storageFeatureId, out var snapshot)
            ? snapshot
            : OwnerFieldSnapshot.Hidden;
    }

    private static void CaptureOwnerGuard(
        EditOperationContext context,
        MetadataV2Resource resource,
        IReadOnlyDictionary<long, OwnerFieldSnapshot>? snapshots,
        long objectId)
    {
        if (!TryCaptureOwnerGuard(context, resource, snapshots, objectId))
        {
            throw new EditNotPermittedException("Edit not permitted.");
        }
    }

    private static bool TryCaptureOwnerGuard(
        EditOperationContext context,
        MetadataV2Resource resource,
        IReadOnlyDictionary<long, OwnerFieldSnapshot>? snapshots,
        long objectId)
    {
        if (!context.RequiresOwnerGuards)
        {
            return true;
        }

        var snapshot = SnapshotFor(snapshots, objectId);
        if (snapshot is not { Visible: true })
        {
            return false;
        }

        var guard = new OwnerEditGuard(
            objectId,
            resource.OwnerEditPolicy!.OwnerField,
            Convert.ToString(snapshot.Value.Owner, CultureInfo.InvariantCulture) ?? string.Empty);
        if (context.OwnerGuards.TryGetValue(objectId, out var previous))
        {
            return previous == guard;
        }

        context.OwnerGuards.Add(objectId, guard);
        return true;
    }

    internal static ImmutableArray<OwnerEditGuard> BuildOwnerGuards(
        FeatureEditBatch batch,
        IReadOnlyDictionary<long, OwnerEditGuard> captured)
    {
        var guards = ImmutableArray.CreateBuilder<OwnerEditGuard>();
        var seen = new HashSet<long>();
        void AddGuard(long objectId)
        {
            if (!seen.Add(objectId))
            {
                return;
            }

            if (!captured.TryGetValue(objectId, out var guard))
            {
                throw new EditBatchValidationException("Edit not permitted.");
            }

            guards.Add(guard);
        }

        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                if (operation.Kind == FeatureEditOperationKind.Update)
                {
                    AddGuard(operation.Feature?.Id
                        ?? throw new EditBatchValidationException("Edit not permitted."));
                }
                else if (operation.Kind == FeatureEditOperationKind.Delete)
                {
                    AddGuard(operation.ObjectId
                        ?? throw new EditBatchValidationException("Edit not permitted."));
                }
            }
        }
        else
        {
            foreach (var feature in batch.Updates)
            {
                AddGuard(feature.Id);
            }

            foreach (var objectId in batch.Deletes)
            {
                AddGuard(objectId);
            }
        }

        return guards.ToImmutable();
    }

    /// <summary>
    /// Routes attribute-rule expressions outside the supported safe subset out of scope by
    /// emitting a structured warning identifying the layer and rule. The edit is allowed to
    /// proceed (full Arcade parity is a non-goal of the edit path).
    /// </summary>
    private sealed class UnsupportedExpressionLogger(ILogger logger) : IUnsupportedExpressionSink
    {
        public void OnUnsupported(MetadataV2Resource resource, MetadataV2AttributeRule rule)
        {
            var resourceId = string.IsNullOrEmpty(resource.Metadata.Id)
                ? (string.IsNullOrEmpty(resource.Metadata.Name) ? "unknown" : resource.Metadata.Name)
                : resource.Metadata.Id;
            FeatureServerLog.AttributeRuleExpressionUnsupported(
                logger,
                resourceId,
                0,
                rule.Name,
                rule.Type.ToString());
        }
    }

    /// <summary>
    /// Verifies that an inbound GeoServices geometry's shape is compatible with the layer's
    /// declared <see cref="MetadataV2GeometryType"/>. ArcGIS feature layers are geometry-type
    /// homogeneous, so an add/update whose geometry shape disagrees with the layer must fail
    /// per-feature rather than be silently stored. The check classifies the geometry by its
    /// populated coordinate members (the GeoServices JSON shape is discriminated by which of
    /// x/y, points, paths, rings, or the envelope bounds are present). Point and multipoint
    /// inputs are accepted on a point layer (a single coordinate is a degenerate multipoint),
    /// and multi-* layer types accept their single-part equivalents.
    /// </summary>
    private static bool IsGeometryTypeCompatible(
        GeoServicesGeometry geometry,
        MetadataV2GeometryType layerGeometryType,
        out string? error)
    {
        error = null;

        // Mixed / collection / unspecified layers do not constrain the geometry shape.
        if (layerGeometryType is MetadataV2GeometryType.None
            or MetadataV2GeometryType.Mixed
            or MetadataV2GeometryType.GeometryCollection)
        {
            return true;
        }

        var inputType = ClassifyGeoServicesGeometry(geometry);
        if (inputType is null)
        {
            // Could not classify (e.g. empty geometry object); leave shape validation to the
            // downstream Esri-JSON / WKB validators rather than rejecting here.
            return true;
        }

        if (IsGeometryShapeCompatible(layerGeometryType, inputType.Value))
        {
            return true;
        }

        error = $"Geometry type {DescribeGeometryType(inputType.Value)} does not match the layer geometry type {DescribeGeometryType(layerGeometryType)}.";
        return false;
    }

    /// <summary>
    /// Classifies a GeoServices geometry object into the canonical geometry-shape family it
    /// represents based on which coordinate members are populated. Returns <c>null</c> when no
    /// recognizable geometry members are present.
    /// </summary>
    private static MetadataV2GeometryType? ClassifyGeoServicesGeometry(GeoServicesGeometry geometry)
    {
        if (geometry.Rings != null)
        {
            return MetadataV2GeometryType.Polygon;
        }

        if (geometry.Paths != null)
        {
            return MetadataV2GeometryType.LineString;
        }

        if (geometry.Points != null)
        {
            return MetadataV2GeometryType.MultiPoint;
        }

        if (geometry.X.HasValue || geometry.Y.HasValue)
        {
            return MetadataV2GeometryType.Point;
        }

        if (geometry.Xmin.HasValue || geometry.Ymin.HasValue
            || geometry.Xmax.HasValue || geometry.Ymax.HasValue)
        {
            // Envelopes are polygonal in shape; only meaningful on polygon layers.
            return MetadataV2GeometryType.Polygon;
        }

        return null;
    }

    /// <summary>
    /// Returns true when an input geometry shape may be stored on a layer of the given type.
    /// A point may be stored on a multipoint layer and vice-versa; line/polygon single- and
    /// multi-part variants are interchangeable, matching how the GeoServices JSON encodes both
    /// single- and multi-part polylines/polygons with the same paths/rings members.
    /// </summary>
    private static bool IsGeometryShapeCompatible(MetadataV2GeometryType layerType, MetadataV2GeometryType inputType)
    {
        return layerType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint
                => inputType is MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString
                => inputType is MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon
                => inputType is MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon,
            _ => false
        };
    }

    private static string DescribeGeometryType(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            _ => geometryType.ToString()
        };

    private static bool TryGetObjectId(Dictionary<string, object?>? attributes, MetadataV2Resource resource, out long objectId)
    {
        objectId = 0;

        if (attributes == null || attributes.Count == 0)
        {
            return false;
        }

        // Not rewritten as .Where(...): this is a first-match short-circuit that returns from
        // inside the loop body (including the Try-pattern conversion result), not a pure filter.
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        foreach (var entry in (attributes).Where(entry => string.Equals(entry.Key, objectIdFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return FeatureServerValueParser.TryConvertToLong(entry.Value, out objectId);
        }

        return false;
    }

    private static EditResult CreateFailureResult(int code, string description, long? objectId = null, string? globalId = null)
    {
        return new EditResult
        {
            ObjectId = objectId,
            GlobalId = globalId,
            Success = false,
            Error = new EditError
            {
                Code = code,
                Description = description
            }
        };
    }

    private static EditResult ConvertEditOperationResult(
        EditOperationResult result,
        FeatureEditOperationKind kind,
        long? responseObjectId = null)
    {
        if (result.IsSuccess)
        {
            return new EditResult
            {
                ObjectId = responseObjectId ?? result.ObjectId,
                GlobalId = result.GlobalId,
                Success = true
            };
        }

        // Map the writer's typed outcome onto the stable per-feature conflict code (#2251)
        // so clients can classify deterministically (e.g. update-update vs delete-delete)
        // without parsing the free-form description.
        return CreateFailureResult(
            GeoServicesEditErrorCodes.ClassifyWriterFailure(result, kind),
            SanitizeEditErrorMessage(result.ErrorMessage, "Operation failed"),
            responseObjectId ?? result.ObjectId,
            result.GlobalId);
    }

    private static void ApplyRollbackResults(EditResult?[]? results, List<int> indexes, List<long>? objectIds, EditError rollbackError)
    {
        if (results == null)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            long? objectId = null;
            if (objectIds != null && i < objectIds.Count)
            {
                objectId = objectIds[i];
            }

            results[indexes[i]] = CreateFailureResult(rollbackError.Code, rollbackError.Description, objectId);
        }
    }

    private static void ApplyResults(
        EditResult?[]? results,
        List<int> indexes,
        ImmutableArray<EditOperationResult> operationResults,
        FeatureEditOperationKind kind,
        List<long>? responseObjectIds = null)
    {
        if (results == null)
        {
            return;
        }

        var count = Math.Min(indexes.Count, operationResults.Length);
        for (var i = 0; i < count; i++)
        {
            var responseObjectId = responseObjectIds != null && i < responseObjectIds.Count
                ? responseObjectIds[i]
                : (long?)null;
            results[indexes[i]] = ConvertEditOperationResult(operationResults[i], kind, responseObjectId);
        }

        for (var i = count; i < indexes.Count; i++)
        {
            results[indexes[i]] ??= CreateFailureResult(GeoServicesEditErrorCodes.GenericFailure, "Operation failed");
        }
    }

    private static void ApplyCreateResponseObjectIds(EditOperationContext context)
    {
        if (context.AddResults == null)
        {
            return;
        }

        for (var i = 0; i < context.CreateIndexes.Count && i < context.CreateResponseObjectIds.Count; i++)
        {
            var responseObjectId = context.CreateResponseObjectIds[i];
            if (!responseObjectId.HasValue)
            {
                continue;
            }

            var result = context.AddResults[context.CreateIndexes[i]];
            if (result is { Success: true })
            {
                result.ObjectId = responseObjectId.Value;
            }
        }
    }

    private static void CaptureCreateEventObjectIds(
        EditOperationContext context,
        ImmutableArray<EditOperationResult> operationResults)
    {
        context.CreateEventObjectIds.Clear();
        for (var i = 0; i < context.CreateIndexes.Count; i++)
        {
            context.CreateEventObjectIds.Add(i < operationResults.Length
                ? operationResults[i].ObjectId
                : null);
        }
    }

    private static EditResult[]? FinalizeResults(EditResult?[]? results)
    {
        if (results == null)
        {
            return null;
        }

        var finalized = new EditResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            finalized[i] = results[i] ?? CreateFailureResult(GeoServicesEditErrorCodes.GenericFailure, "Operation failed");
        }

        return finalized;
    }

    private static bool AreAllResultsSuccessful(EditResult[]? results)
    {
        if (results == null)
        {
            return true;
        }

        return results.All(result => result.Success);
    }

    // Edit error text reflected to clients is sanitized through the shared cross-cutting
    // sanitizer so the GeoServices edit path enforces the same SQL/credential,
    // provider-internal, parser-diagnostic, control-character, and length guarantees as
    // every other protocol adapter (e.g. the OGC API Features filter path).
    private static string SanitizeEditErrorMessage(string? message, string fallback)
        => ErrorMessageSanitizer.Sanitize(message, fallback, MaxSafeEditErrorMessageLength);

    /// <summary>
    /// Raised inside the per-feature build path when an owner-based edit policy denies the
    /// edit. Distinct from <see cref="ArgumentException"/> (used for invalid-data validation
    /// failures) so the surrounding per-feature catch can classify the failure as
    /// <see cref="GeoServicesEditErrorCodes.NotPermitted"/> rather than
    /// <see cref="GeoServicesEditErrorCodes.ValidationFailed"/>.
    /// </summary>
    private sealed class EditNotPermittedException(string message) : Exception(message)
    {
    }

    /// <summary>
    /// Batch-level edit validation that must surface as a client error rather than a 500
    /// (honua-server#6133). Per-feature failures use <see cref="ArgumentException"/> inside
    /// the slot loop instead.
    /// </summary>
    internal sealed class EditBatchValidationException(string message) : Exception(message)
    {
    }

    /// <summary>
    /// Opens one writer transaction for every calculate batch in a single request.
    /// </summary>
    internal Task<IFeatureWriterTransaction> BeginSharedEditTransactionAsync(CancellationToken cancellationToken)
        => _featureWriter.BeginTransactionAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Rolls back a calculate transaction and releases idempotency reservations for batches
    /// that were only staged.
    /// </summary>
    internal async Task RollbackAmbientEditTransactionAsync(AmbientFeatureEditTransaction ambient)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        try
        {
            await ambient.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAmbientReservationsAsync(ambient).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commits a calculate transaction, then records idempotency responses and runs the
    /// post-commit hooks that were deferred while the batches were only staged.
    /// </summary>
    /// <returns><see langword="false"/> when the commit acknowledgement was lost. Reservations
    /// stay held so a retry cannot apply the same rows again. A confirmed commit is durable
    /// before any receipt or hook await: a failure there keeps the reservation and still
    /// returns <see langword="true"/>.</returns>
    internal async Task<bool> CommitAmbientEditTransactionAsync(
        AmbientFeatureEditTransaction ambient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        var outcome = await ambient.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (outcome != FeatureWriterTransactionCommitOutcome.Committed)
        {
            return false;
        }

        // The caller's finally rolls this transaction back and releases reservations while
        // PreserveReservations is false. Set it before the receipt and hook awaits so a throw
        // cannot turn a durable calculate into a second apply on retry (#6359).
        ambient.PreserveReservations = true;

        var recordedTokens = new HashSet<string>(ambient.Recorded.Count, StringComparer.Ordinal);
        var failedReceiptTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (scope, token, response) in ambient.Recorded)
        {
            try
            {
                await _idempotencyStore.SetAsync(scope, token, response, CancellationToken.None).ConfigureAwait(false);
                recordedTokens.Add(token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The rows are already durable. Leave this reservation held so the retry
                // conflicts instead of applying the same calculate again.
                failedReceiptTokens.Add(token);
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(
                    _logger, scope.ServiceId, scope.LayerId, ex);
            }
        }

        foreach (var (scope, token) in ambient.HeldReservations)
        {
            if (recordedTokens.Contains(token) || failedReceiptTokens.Contains(token))
            {
                continue;
            }

            try
            {
                await _idempotencyStore.ReleaseAsync(scope, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(
                    _logger, scope.ServiceId, scope.LayerId, ex);
            }
        }

        ambient.HeldReservations.Clear();
        foreach (var action in ambient.AfterCommit)
        {
            try
            {
                await action(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.CalculatePostCommitHookFailed(_logger, ex);
            }
        }

        ambient.AfterCommit.Clear();
        return true;
    }

    private async Task ReleaseAmbientReservationsAsync(AmbientFeatureEditTransaction ambient)
    {
        foreach (var (scope, token) in ambient.HeldReservations)
        {
            await _idempotencyStore.ReleaseAsync(scope, token).ConfigureAwait(false);
        }

        ambient.HeldReservations.Clear();
        ambient.Recorded.Clear();
        ambient.AfterCommit.Clear();
    }

    private async Task RunPostCommitSideEffectsAsync(
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        CancellationToken cancellationToken = default)
    {
        using var postCommitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        postCommitCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _mutationEventService.InvalidateLayerAsync(serviceId, layerId, postCommitCts.Token);
            await PublishFeatureChangeEventsAsync(serviceId, layerId, context, postCommitCts.Token);
            await RunPluginAfterHooksAsync(serviceId, layerId, resource, context, postCommitCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (postCommitCts.IsCancellationRequested)
        {
            FeatureServerLog.PostCommitSideEffectsTimedOut(_logger, serviceId, layerId);
        }
    }
}

/// <summary>
/// One feature-writer transaction shared by every batch of a single calculate request.
/// Batches stage writes here and commit only after the last batch succeeds (#6359).
/// </summary>
internal sealed class AmbientFeatureEditTransaction
{
    public required IFeatureWriterTransaction Transaction { get; init; }

    internal List<(ApplyEditsIdempotencyScope Scope, string Token)> HeldReservations { get; } = [];

    internal List<(ApplyEditsIdempotencyScope Scope, string Token, ApplyEditsResponse Response)> Recorded { get; } = [];

    internal List<Func<CancellationToken, Task>> AfterCommit { get; } = [];

    /// <summary>
    /// When <see langword="true"/>, disposal must not roll the transaction back or release
    /// reservations. Set after a commit whose acknowledgement was lost.
    /// </summary>
    internal bool PreserveReservations { get; set; }
}
