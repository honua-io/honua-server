// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Assets;

internal sealed class AssetProfileService(IAssetProfileStore store, AssetProfileValidator validator, TimeProvider clock, AssetCompletionCapabilities capabilities)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<IResult> ListAsync(HttpContext context)
    {
        var grant = await AssetServiceAccess.RequireEditorAsync(context, context.Request.Query["serviceId"].ToString()).ConfigureAwait(false);
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
        var result = await store.ListAsync(new AssetProfileQuery(grant.ServiceId, after, limit), context.RequestAborted).ConfigureAwait(false);
        var items = new List<AssetProfile>(result.Items.Count);
        foreach (var profile in result.Items)
        {
            items.Add(await capabilities.DescribeAsync(profile, context.RequestAborted).ConfigureAwait(false));
        }
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(result with { Items = items }, AssetsJsonContext.Default.AssetProfileListResult);
    }

    public async Task<IResult> GetAsync(HttpContext context, Guid profileId)
    {
        var profile = await store.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        if (profile is null || (await AssetServiceAccess.RequireEditorAsync(context, profile.ServiceId).ConfigureAwait(false)).Denied is not null)
        {
            return NotFound(context);
        }

        profile = await capabilities.DescribeAsync(profile, context.RequestAborted).ConfigureAwait(false);
        AssetCommandHttp.ApplyNoStore(context.Response);
        return Results.Json(profile, AssetsJsonContext.Default.AssetProfile);
    }

    public async Task<IResult> CreateAsync(HttpContext context)
    {
        var body = await AssetCommandHttp.ReadAsync(context, AssetsJsonContext.Default.AssetProfileWriteRequest).ConfigureAwait(false);
        if (body?.CommandId is not Guid commandId)
        {
            return AssetCommandHttp.Rejected(context, "commandId", "A JSON body with a commandId is required.");
        }

        var grant = await AssetServiceAccess.RequireEditorAsync(context, body.ServiceId).ConfigureAwait(false);
        if (grant.Denied is { } denied)
        {
            return denied;
        }

        var request = body with { ServiceId = grant.ServiceId };
        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, grant.ServiceId, "create", Guid.Empty),
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
        if (request?.CommandId is not Guid commandId || request.ExpectedVersion is not long expectedVersion)
        {
            return AssetCommandHttp.Rejected(context, "expectedVersion", "A JSON body with commandId and expectedVersion is required.");
        }

        var current = await store.GetAsync(profileId, context.RequestAborted).ConfigureAwait(false);
        var grant = current is null ? null : await AssetServiceAccess.RequireEditorAsync(context, current.ServiceId).ConfigureAwait(false);
        if (current is null || grant!.Denied is not null)
        {
            return NotFound(context);
        }

        var command = AssetCommandHttp.CreateCommand(
            context,
            new CommandSubject(commandId, current.ServiceId, "update", profileId),
            (request, AssetsJsonContext.Default.AssetProfileWriteRequest));
        if (await store.FindReceiptAsync(command, context.RequestAborted).ConfigureAwait(false) is { } replay)
        {
            return AssetCommandHttp.ToResult(context, replay, StatusCodes.Status200OK);
        }

        // Omitted fields are filled from this read, so it must be the version the caller edited; the store re-checks under lock.
        if (current.Version != expectedVersion)
        {
            return AssetCommandHttp.ToResult(context, AssetCommandResult.VersionConflict(commandId, current.Version, "asset profile"), StatusCodes.Status200OK);
        }

        if (RefuseImmutableChange(request, current, grant) is { } refused)
        {
            return AssetCommandHttp.Rejected(context, [refused]);
        }

        var merged = Merge(request, current);
        var validation = await validator.ValidateAsync(merged, grant.Service!, context.RequestAborted).ConfigureAwait(false);
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
        var result = await store.UpdateAsync(updated, expectedVersion, command, context.RequestAborted).ConfigureAwait(false);
        return AssetCommandHttp.ToResult(context, result, StatusCodes.Status200OK);
    }

    // A profile never moves layers, and tasks, records and summaries key on its asset ID values.
    private static AssetFieldError? RefuseImmutableChange(AssetProfileWriteRequest request, AssetProfile current, AssetServiceGrant grant)
    {
        if ((request.ServiceId is not null && !AssetServiceAccess.Matches(grant.Service!, request.ServiceId)) ||
            (request.LayerId ?? current.LayerId) != current.LayerId)
        {
            return new AssetFieldError("layerId", "A profile cannot move to another layer; create a new profile.");
        }

        return request.Fields?.AssetId is { } assetIdField && !string.Equals(assetIdField, current.Fields.AssetId, StringComparison.OrdinalIgnoreCase)
            ? new AssetFieldError("fields.assetId", "The asset ID field cannot change; create a new profile.")
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
            InspectionFormId = request.InspectionFormId,
            ServiceFormId = request.ServiceFormId,
            State = request.State ?? AssetProfileState.Draft,
            Version = 1,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedBy = actor,
            UpdatedAt = now,
        };
    }

    private static IResult NotFound(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The asset profile was not found.");
}
