// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Validation;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private readonly record struct AttachmentTarget(GrpcLayerContext Layer, long FeatureId);

    private async Task<AttachmentTarget> ResolveAttachmentParentAsync(
        Proto.AttachmentParent? parent, ServerCallContext context)
    {
        if (parent is null || string.IsNullOrWhiteSpace(parent.ServiceId) || parent.LayerId < 0)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "An attachment parent is required.");
        }
        var layer = await ValidateGrpcLayerAsync(parent.ServiceId, parent.LayerId, context.CancellationToken).ConfigureAwait(false);
        await EnsureReadAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = parent.ServiceId, LayerId = parent.LayerId, ResultRecordCountLong = 2
        };
        SetAttachmentParentSelector(request, parent, layer);
        var queryContext = await CreateQueryContextAsync(
            request, layer, context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        var query = queryContext.Query with
        {
            PublicIdAttributeName = layer.ObjectIdFieldName, ReturnPublicObjectIds = false, ExcludeAttributes = true
        };
        return new AttachmentTarget(layer, await ResolveAttachmentFeatureAsync(layer, query, context).ConfigureAwait(false));
    }

    private static void SetAttachmentParentSelector(
        Proto.QueryFeaturesRequest request, Proto.AttachmentParent parent, GrpcLayerContext layer)
    {
        if (parent.IdentityCase == Proto.AttachmentParent.IdentityOneofCase.ObjectId && parent.ObjectId > 0)
        {
            request.ObjectIds.Add(parent.ObjectId);
            return;
        }
        if (parent.IdentityCase != Proto.AttachmentParent.IdentityOneofCase.GlobalId ||
            !Guid.TryParse(parent.GlobalId, out var globalId) || globalId == Guid.Empty)
        {
            throw AttachmentError(StatusCode.InvalidArgument, "A valid parent identity is required.");
        }
        var field = layer.Resource.Editing?.GlobalIdField;
        if (string.IsNullOrWhiteSpace(field) || !layer.AttributeFields.Any(value => value.Name == field))
        {
            throw AttachmentError(StatusCode.InvalidArgument, "The parent has no published global identifier.");
        }
        request.Where = $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\" = '{globalId:D}'";
    }

    private async Task<long> ResolveAttachmentFeatureAsync(
        GrpcLayerContext layer, FeatureQuery query, ServerCallContext context)
    {
        var reader = await ResolveReaderAsync(layer, FeatureProviderReadOperation.Query,
            context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        var result = await reader.QueryAsync(layer.StorageLayerId, query, context.CancellationToken).ConfigureAwait(false);
        if (result.Features.Length != 1)
        {
            throw AttachmentError(StatusCode.NotFound, "The attachment parent was not found.");
        }
        return result.Features[0].Id;
    }

    private static async Task EnsureAttachmentEditAccessAsync(
        GrpcLayerContext layer, ServerCallContext context, AuthorizationOperation operation)
    {
        var decision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
            context.GetHttpContext(), layer.Resource, layer.Service, operation, context.CancellationToken).ConfigureAwait(false);
        ThrowIfAccessDenied(decision);
        if (LayerValidationHelpers.FindUndeclaredEditCapability(layer.Service, layer.Publication, operation) is not null ||
            layer.Resource.Editing is { CanModify: false } || layer.Resource.Editing?.SupportsAttachments != true)
        {
            throw AttachmentError(StatusCode.FailedPrecondition, "Attachment editing is not enabled for this layer.");
        }
    }

    private async Task<AttachmentUploadSession> ResolveAttachmentSessionAsync(string id, ServerCallContext context)
    {
        if (!Guid.TryParse(id, out var sessionId))
        {
            throw AttachmentError(StatusCode.InvalidArgument, "A valid upload session identifier is required.");
        }
        var key = new AttachmentUploadSessionKey(AttachmentOwner(context), sessionId);
        var session = await AttachmentService<IAttachmentUploadSessionStore>(context).GetAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (session.Request.PublishedLayerId is not { } publishedId)
        {
            throw AttachmentError(StatusCode.FailedPrecondition, "The upload has no published layer binding.");
        }
        var layer = await ValidateGrpcLayerAsync(session.Request.ServiceId, publishedId, context.CancellationToken).ConfigureAwait(false);
        if (layer.StorageLayerId != session.Request.LayerId)
        {
            throw AttachmentError(StatusCode.FailedPrecondition, "The upload layer binding has changed.");
        }
        await EnsureReadAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);
        await EnsureAttachmentEditAccessAsync(layer, context, AuthorizationOperation.Update).ConfigureAwait(false);
        var queryContext = await CreateQueryContextAsync(new Proto.QueryFeaturesRequest
        {
            ServiceId = session.Request.ServiceId, LayerId = publishedId, ResultRecordCountLong = 2
        }, layer, context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        await ResolveAttachmentFeatureAsync(layer, queryContext.Query with
        {
            ObjectIds = [session.Request.FeatureId], PublicIdAttributeName = null,
            ReturnPublicObjectIds = false, ExcludeAttributes = true
        }, context).ConfigureAwait(false);
        return session;
    }

    private static string AttachmentOwner(ServerCallContext context)
        => IdempotencyCaller.Resolve(context.GetHttpContext())?.Fingerprint()
            ?? throw AttachmentError(StatusCode.FailedPrecondition, IdempotencyCaller.UnbindableCallerMessage);

    private static T AttachmentService<T>(ServerCallContext context) where T : notnull
        => context.GetHttpContext().RequestServices.GetRequiredService<T>();

    private static RpcException AttachmentError(StatusCode code, string message) => new(new Status(code, message));
}
