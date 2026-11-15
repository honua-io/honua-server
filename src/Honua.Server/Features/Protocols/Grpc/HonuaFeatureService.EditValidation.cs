// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private readonly record struct PreparedGrpcEdit(EditCall Call, FeatureEditBatch Batch, GlobalIdEdit GlobalIds)
    {
        public FeatureEditBatch RequestBatch { get; init; }
    }

    private async Task<PreparedGrpcEdit> PrepareGrpcEditAsync(EditCall call)
    {
        var batch = ToEditBatch(call.Request);
        var layer = call.Layer;
        await EnsureWriteAccessAsync(call.Context, layer.Service, layer.Resource, batch).ConfigureAwait(false);
        EnsureDeclaredEditCapabilities(layer.Service, layer.Publication, batch);
        EnsureEditLimits(batch, _editLimits);
        (batch, var globalIds) = await PrepareGlobalIdsAsync(call, batch).ConfigureAwait(false);
        batch = await ValidateGrpcMutationsAsync(call, batch).ConfigureAwait(false);
        if (layer.Snapshot is { } snapshot)
        {
            var binding = snapshot.ResolveStorageBinding(layer.Publication)
                ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The layer has no resolvable storage binding."));
            if (!FeatureStorageMapping.FromMetadata(layer.Resource, binding).SupportsManagedWrites)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "The layer's storage binding does not support managed writes."));
            }
        }
        return new PreparedGrpcEdit(call, batch, globalIds);
    }

    private async Task<PreparedGrpcEdit> PrepareGrpcTargetsAsync(PreparedGrpcEdit edit)
    {
        var call = edit.Call;
        var batch = await ResolveGlobalIdTargetsAsync(edit.GlobalIds, edit.Batch, call.Context.CancellationToken)
            .ConfigureAwait(false);
        var requestBatch = batch;
        batch = await EnsureEditTargetsVisibleAsync(call.Layer.StorageLayerId, batch, call.Context.CancellationToken)
            .ConfigureAwait(false);
        var owner = await OwnerEditPolicyFieldAccess.PrepareBatchAsync(
            _featureReader, call.Layer.StorageLayerId, call.Layer.Resource.OwnerEditPolicy, batch,
            ServiceDataEditorAuthorization.ResolveEditPrincipal(call.Context.GetHttpContext()), call.Context.CancellationToken)
            .ConfigureAwait(false);
        if (!owner.IsAllowed)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, owner.DenialReason ?? "Edit not permitted."));
        }
        return edit with { Batch = owner.Batch, RequestBatch = requestBatch };
    }

    private async Task<FeatureEditBatch> ValidateGrpcMutationsAsync(
        EditCall call,
        FeatureEditBatch batch)
    {
        var geometryValidator = call.Context.GetHttpContext()?.RequestServices.GetService<IGeometryValidator>()
            ?? new WkbGeometryValidator(
                Options.Create(new LimitsOptions()),
                new Honua.Core.Features.FeatureStore.ReadOnlyProviders.ReadOnlyGeometryTopologyValidator("gRPC"),
                _logger);
        var mutationValidator = new FeatureMutationValidator(geometryValidator);
        var validation = new GrpcMutationValidation(call, mutationValidator, GrpcMutationMode.Create);
        var creates = await ValidateGrpcFeaturesAsync(batch.Creates, validation).ConfigureAwait(false);
        var updates = await ValidateGrpcFeaturesAsync(batch.Updates, validation with { Mode = GrpcMutationMode.Update })
            .ConfigureAwait(false);
        return batch with { Creates = creates, Updates = updates };
    }

    private enum GrpcMutationMode { Create, Update }

    private readonly record struct GrpcMutationValidation(
        EditCall Call, FeatureMutationValidator Validator, GrpcMutationMode Mode);

    private static async Task<ImmutableArray<Feature>> ValidateGrpcFeaturesAsync(
        ImmutableArray<Feature> features, GrpcMutationValidation validation)
    {
        if (features.IsDefaultOrEmpty)
        {
            return features.IsDefault ? ImmutableArray<Feature>.Empty : features;
        }
        var validated = ImmutableArray.CreateBuilder<Feature>(features.Length);
        foreach (var feature in features)
        {
            var geometry = await ValidateGrpcGeometryAsync(feature.Geometry, validation).ConfigureAwait(false);
            var attributes = validation.Validator.ValidateAttributes(validation.Call.Layer.Resource,
                feature.Attributes, ValidationExtensions.AttributeValidationMode.Strict,
                validation.Mode == GrpcMutationMode.Update);
            if (!attributes.IsValid)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    BoundEditValidationMessage(attributes.ErrorMessage, "Invalid attributes.")));
            }
            validated.Add(feature with { Geometry = geometry, Attributes = attributes.Value ?? feature.Attributes });
        }
        return validated.MoveToImmutable();
    }

    private static async Task<byte[]?> ValidateGrpcGeometryAsync(byte[]? geometry, GrpcMutationValidation validation)
    {
        if (geometry is not { Length: > 0 })
        {
            return geometry;
        }
        var result = await validation.Validator.ValidateGeometryAsync(validation.Call.Layer.Resource,
            geometry, validation.Call.Context.CancellationToken).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                BoundEditValidationMessage(result.ErrorMessage, "Invalid geometry.")));
        }
        return result.Geometry;
    }

    private static string BoundEditValidationMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int maxLength = 240;
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }
}
