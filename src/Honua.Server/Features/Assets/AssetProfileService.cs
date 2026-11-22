// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Assets;

internal sealed class AssetProfileService(IAssetProfileStore store, AssetProfileValidator validator, TimeProvider clock)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<IResult> ListAsync(HttpContext context)
    {
        var grant = await AssetServiceAccess.ResolveServiceAsync(context, context.Request.Query["serviceId"].ToString()).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var cursor = context.Request.Query["cursor"].ToString();
        Guid? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Guid.TryParse(cursor, out var parsed))
            {
                return AssetCommandHttp.Rejected(context, "cursor", "cursor is not valid.");
            }

            after = parsed;
        }

        var limit = int.TryParse(context.Request.Query["limit"], out var requested) ? Math.Clamp(requested, 1, MaxLimit) : DefaultLimit;
        var allowedLayers = await AssetServiceAccess.AuthorizedLayerIdsAsync(context, grant).ConfigureAwait(false);
        var result = await store.ListAsync(
            new AssetProfileQuery(grant.ServiceId, after, limit) { AllowedLayerIds = allowedLayers },
            context.RequestAborted).ConfigureAwait(false);

        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(result, AssetsJsonContext.Default.AssetProfileListResult);
    }

    public async Task<IResult> GetAsync(HttpContext context, Guid profileId)
    {
        var profile = await store.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        if (profile is null || (await AssetServiceAccess.RequireLayerEditorAsync(context, profile.ServiceId, profile.LayerId).ConfigureAwait(false)).Denied is not null)
        {
            return NotFound(context);
        }

        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(profile, AssetsJsonContext.Default.AssetProfile);
    }

    public async Task<IResult> CreateAsync(HttpContext context)
    {
        var body = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.AssetProfileWriteRequest).ConfigureAwait(false);
        if (body?.CommandId is not Guid commandId || commandId == Guid.Empty)
        {
            return AssetCommandHttp.Rejected(context, "commandId", "A JSON body with a non-empty commandId is required.");
        }

        var grant = await AssetServiceAccess.RequireLayerEditorAsync(context, body.ServiceId, body.LayerId).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var request = body with { ServiceId = grant.ServiceId };
        if (request.State == AssetProfileState.Archived)
        {
            return AssetCommandHttp.Rejected(context, "state", "A new profile starts as draft or active.");
        }

        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, grant.ServiceId, ("create", Guid.Empty)),
            (request, AssetsJsonContext.Default.AssetProfileWriteRequest));
        if (await store.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status201Created);
        }

        var validation = await validator.ValidateAsync(request, grant.Service!, context.RequestAborted).ConfigureAwait(false);
        if (validation.Errors.Count > 0)
        {
            return AssetCommandHttp.Rejected(context, validation.Errors);
        }

        var result = await store.CreateAsync(NewProfile(request, validation, command.Actor), command, context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status201Created);
    }

    public async Task<IResult> UpdateAsync(HttpContext context, Guid profileId)
    {
        var request = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.AssetProfileWriteRequest).ConfigureAwait(false);
        if (request?.CommandId is not Guid commandId || commandId == Guid.Empty)
        {
            return AssetCommandHttp.Rejected(context, "commandId", "A JSON body with a non-empty commandId is required.");
        }

        if (request.ExpectedVersion is not long expectedVersion)
        {
            return AssetCommandHttp.Rejected(context, "expectedVersion", "expectedVersion is required.");
        }

        var current = await store.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        var grant = current is null ? null : await AssetServiceAccess.RequireLayerEditorAsync(context, current.ServiceId, current.LayerId).ConfigureAwait(false);
        if (current is null || grant!.Denied is not null)
        {
            return NotFound(context);
        }

        var command = AssetCommandHttp.CreateCommand(context, new CommandSubject(commandId, current.ServiceId, ("update", profileId)),
            (ReplayKey(request, current, grant), AssetsJsonContext.Default.AssetProfileWriteRequest));
        if (await store.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        // Omitted fields are filled from this read, so it must be the version the caller edited; the store re-checks under lock.
        if (current.Version != expectedVersion)
        {
            return AssetCommandHttp.ToResult(context, AssetCommandResult.VersionConflict(commandId, current.Version, "asset profile"), StatusCodes.Status200OK);
        }

        return RefuseImmutableChange(request, current, grant) is { } refused
            ? AssetCommandHttp.Rejected(context, [refused])
            : await ValidateAndUpdateAsync(context, new AssetProfileUpdate(current, expectedVersion, command), (request, grant.Service!)).ConfigureAwait(false);
    }

    private async Task<IResult> ValidateAndUpdateAsync(
        HttpContext context, AssetProfileUpdate update, (AssetProfileWriteRequest Request, MetadataV2Service Service) input)
    {
        var (request, service) = input;
        var (current, expectedVersion, command) = update;
        var merged = Merge(request, current);
        var validation = await validator.ValidateAsync(merged, service, context.RequestAborted).ConfigureAwait(false);
        if (validation.Errors.Count > 0)
        {
            return AssetCommandHttp.Rejected(context, validation.Errors);
        }

        var updated = NewProfile(merged, validation, command.Actor) with
        {
            ProfileId = current.ProfileId,
            Version = current.Version,
            CreatedBy = current.CreatedBy,
            CreatedAt = current.CreatedAt,
        };
        var result = await store.UpdateAsync(new AssetProfileUpdate(updated, expectedVersion, command), context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

    // A retry may name the service by name or ID in any case; the receipt hash uses the stored service ID, as create does.
    private static AssetProfileWriteRequest ReplayKey(AssetProfileWriteRequest request, AssetProfile current, AssetServiceGrant grant)
        => request.ServiceId is not null && AssetServiceAccess.Matches(grant.Service!, request.ServiceId)
            ? request with { ServiceId = current.ServiceId }
            : request;

    // One profile occupies a layer for good (the schema unique key). A draft may still correct its asset ID
    // field, because nothing has keyed tasks or summaries on it yet. Later states may not.
    private static AssetFieldError? RefuseImmutableChange(AssetProfileWriteRequest request, AssetProfile current, AssetServiceGrant grant)
    {
        if (current.State == AssetProfileState.Archived)
        {
            var message = request.State is { } state && state != AssetProfileState.Archived
                ? "An archived profile cannot be reopened. The layer keeps this profile."
                : "An archived profile is read-only and cannot be updated.";
            return new AssetFieldError("state", message);
        }

        if (current.State == AssetProfileState.Active && request.State == AssetProfileState.Draft)
        {
            return new AssetFieldError("state", "An active profile cannot return to draft.");
        }

        if (request.ServiceId is not null && !AssetServiceAccess.Matches(grant.Service!, request.ServiceId))
        {
            return new AssetFieldError("serviceId", "A profile cannot move to another service. The layer keeps this profile.");
        }

        if ((request.LayerId ?? current.LayerId) != current.LayerId)
        {
            return new AssetFieldError("layerId", "A profile cannot move to another layer. The layer keeps this profile.");
        }

        return request.Fields?.AssetId is { } assetIdField
            && !string.Equals(assetIdField, current.Fields.AssetId, StringComparison.OrdinalIgnoreCase)
            && current.State != AssetProfileState.Draft
            ? new AssetFieldError("fields.assetId", "The asset ID field cannot change after the profile leaves draft.")
            : null;
    }

    // Omitted (null) values keep the stored value, field by field; a binding or mapping is never cleared implicitly.
    private static AssetProfileWriteRequest Merge(AssetProfileWriteRequest request, AssetProfile current) => request with
    {
        ServiceId = current.ServiceId,
        LayerId = current.LayerId,
        Name = request.Name ?? current.Name,
        Fields = new AssetProfileFieldMap
        {
            AssetId = request.Fields?.AssetId ?? current.Fields.AssetId,
            Condition = request.Fields?.Condition ?? current.Fields.Condition,
            Status = request.Fields?.Status ?? current.Fields.Status,
            LastInspected = request.Fields?.LastInspected ?? current.Fields.LastInspected,
            LastServiced = request.Fields?.LastServiced ?? current.Fields.LastServiced,
        },
        ConditionLabels = request.ConditionLabels ?? current.ConditionLabels,
        InspectionFormId = request.InspectionFormId ?? current.InspectionFormId,
        ServiceFormId = request.ServiceFormId ?? current.ServiceFormId,
        State = request.State ?? current.State,
    };

    private AssetProfile NewProfile(AssetProfileWriteRequest request, AssetProfileValidation validation, string actor)
    {
        var now = clock.GetUtcNow();
        return new AssetProfile
        {
            ProfileId = Guid.NewGuid(),
            ServiceId = request.ServiceId!,
            LayerId = request.LayerId!.Value,
            Name = request.Name!.Trim(),
            GeometryType = validation.GeometryType!,
            Fields = validation.Fields!,
            ConditionLabels = request.ConditionLabels!,
            InspectionFormId = NormalizeOptional(request.InspectionFormId),
            ServiceFormId = NormalizeOptional(request.ServiceFormId),
            State = request.State ?? AssetProfileState.Draft,
            Version = 1,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedBy = actor,
            UpdatedAt = now,
        };
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult NotFound(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The asset profile was not found.");
}
