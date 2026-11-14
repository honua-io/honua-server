// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

// Undo for an unpublished staged import: drops only the server-owned table that the same tenant,
// requester and intent imported, after a person approves an agent's request.
internal sealed class StagedImportDiscardExecutor(IServiceProvider services)
    : Honua.Core.Features.Operations.Abstractions.IOperationExecutor
{
    public string OperationId => StagedImportOperation.DiscardOperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        string? problem = null;
        try
        {
            _ = StagedImportOperation.Required(request, "intentId");
            _ = StagedImportOperation.RequireIdentifier(request, "schema");
        }
        catch (GeoprocessingValidationException exception)
        {
            problem = exception.Message;
        }

        return Task.FromResult(new OperationValidation
        {
            IsValid = problem is null,
            Status = problem is null ? "valid" : "invalid",
            Messages = problem is null ? [] : [problem],
        });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        _ = await StagedImportOperation.AuthorizedPrincipalAsync(services, cancellationToken).ConfigureAwait(false);
        var intentId = StagedImportOperation.Required(request, "intentId");
        var schema = StagedImportOperation.RequireIdentifier(request, "schema");
        var ledger = services.GetRequiredService<IServerJobArtifactImportLedger>();
        var outcome = await ledger.DiscardStagedAsync(
            new StagedImportDiscardRequest(tenantId, actorId, intentId, schema,
                StagedImportOperation.DestinationTable(tenantId, actorId, intentId)),
            cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            StagedImportDiscardOutcome.Referenced => throw new GeoprocessingPreconditionFailedException(
                "The staged table is published or referenced; unpublish it before discarding."),
            StagedImportDiscardOutcome.NotOwned => throw new GeoprocessingPreconditionFailedException(
                "The staged table is not importer-owned; it was not dropped."),
            _ => StagedImportHandles.Completed(services, context, new StagedImportResult(OperationId,
                outcome == StagedImportDiscardOutcome.Discarded
                    ? "Discarded the staged import."
                    : "No staged import remains for this intent.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["intentId"] = intentId,
                    ["outcome"] = outcome == StagedImportDiscardOutcome.Discarded ? "discarded" : "not-found",
                })),
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(StagedImportHandles.Status(handle));
}

internal sealed class StagedImportDiscardApprovalMapper : IOperationApprovalRequestMapper
{
    public string OperationId => StagedImportOperation.DiscardOperationId;

    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
        {
            throw new ArgumentException("The mapper only accepts dataset.stage.discard requests.", nameof(request));
        }

        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var intentId = StagedImportOperation.Required(request, "intentId");
        var schema = StagedImportOperation.RequireIdentifier(request, "schema");
        return StagedApprovalRequests.Build(new StagedApprovalInput
        {
            OperationId = OperationId,
            Request = request,
            Context = context,
            Decision = decision,
            Summary = $"Drop staging table '{schema}.imported_{StagedImportOperation.DestinationTable(tenantId, actorId, intentId)}' "
                + $"from staged import '{intentId}' and keep its spent receipt.",
        });
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
        => StagedApprovalRequests.ReadReplay(OperationId, request);
}
