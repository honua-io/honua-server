// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Maps canonical typed operation identities to the OAuth operation used to constrain replay.
/// Unknown identities deliberately have no mapping: an approval cannot invent scope authority.
/// </summary>
public static class OperationScopeMapping
{
    /// <summary>Resolves the canonical OAuth operation for an approved replay.</summary>
    public static bool TryResolve(OperationRequest request, out OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.GatewayRequest is { } gateway)
        {
            operation = gateway.Kind switch
            {
                OperationClass.Deploy when string.Equals(
                    gateway.ActionDiscriminator, "deploy.rollback", StringComparison.Ordinal) => OperatorOperation.Rollback,
                OperationClass.Deploy => OperatorOperation.Publish,
                OperationClass.MetadataRelease => OperatorOperation.Publish,
                OperationClass.Seed => OperatorOperation.Create,
                OperationClass.Geoprocess => OperatorOperation.ExecuteMutatingProcess,
                OperationClass.ServicePublish => OperatorOperation.Publish,
                OperationClass.StudioDraftMutation => ResolveStudioOperation(request.OperationId),
                _ => default,
            };

            return gateway.Kind != OperationClass.AdminConfigChange
                && (gateway.Kind != OperationClass.StudioDraftMutation || IsStudioOperation(request.OperationId));
        }

        operation = request.OperationId switch
        {
            "service.publish" => OperatorOperation.Publish,
            "studio.draft.create" => OperatorOperation.Create,
            "studio.draft.update" or "studio.draft.save-version" => OperatorOperation.Update,
            "studio.draft.delete" => OperatorOperation.Delete,
            "admin.layer.publish" => OperatorOperation.Publish,
            "admin.connections.create" or "admin.import.upload" or "admin.import.upload-url" => OperatorOperation.Create,
            "admin.connections.delete" or "admin.import.jobs.cancel" => OperatorOperation.Delete,
            "admin.layer.set-enabled" or "admin.layer.fields.set" or "admin.layer.filter.set" or
                "admin.layer.popup-info.set" or "admin.layer.drawing-info.set" or
                "admin.layer.style.set" or "admin.layer.style.import-sld" or
                "admin.services.protocols.set" or "admin.services.access-policy.set" or
                "admin.services.timeinfo.set" or "admin.services.layer-metadata.set" or
                "admin.connections.update" or "admin.connections.extents.refresh" or
                "admin.connections.features.refresh" => OperatorOperation.Update,
            _ => default,
        };
        return operation != default;
    }

    private static bool IsStudioOperation(string operationId)
        => operationId is "studio.draft.create" or "studio.draft.update" or
            "studio.draft.save-version" or "studio.draft.delete";

    private static OperatorOperation ResolveStudioOperation(string operationId)
        => operationId switch
        {
            "studio.draft.create" => OperatorOperation.Create,
            "studio.draft.update" or "studio.draft.save-version" => OperatorOperation.Update,
            "studio.draft.delete" => OperatorOperation.Delete,
            _ => default,
        };
}
