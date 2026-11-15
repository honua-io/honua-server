// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Shared.Models;
using Honua.ServiceDefaults;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private async Task<Proto.ApplyServiceEditsResponse> ExecuteServiceEditsAsync(ServiceEditExecution execution)
    {
        var call = execution.Call;
        await using var transaction = await BeginServiceTransactionAsync(call.Context).ConfigureAwait(false);
        if (execution.Keyed is not null && !transaction.SupportsDurableReceipts)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The transaction provider does not support durable service edit receipts."));
        }
        var results = await ApplyServiceLayersAsync(execution, transaction).ConfigureAwait(false);
        var rolledBack = call.Request.RollbackOnFailure && results.Any(result => result.HasErrors);
        if (rolledBack)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                results[index] = FeatureEditResult.Rollback(result.CreateResults, result.UpdateResults, result.DeleteResults);
            }
        }
        var layerResults = new ServiceEditResults(results,
            rolledBack ? ServiceEditOutcome.RolledBack : ServiceEditOutcome.Committed);
        var response = BuildServiceEditResponse(execution.Edits, layerResults);
        var outcome = await CommitServiceEditsAsync(transaction, execution, response).ConfigureAwait(false);
        if (outcome != FeatureWriterTransactionCommitOutcome.Committed)
        {
            return await ReplayServiceReceiptAsync(execution).ConfigureAwait(false);
        }
        execution.Keyed?.Complete(response, execution.Fingerprint);
        if (!rolledBack)
        {
            await PublishServiceChangesAsync(execution, results).ConfigureAwait(false);
        }
        return response;
    }

    private async Task<FeatureEditResult[]> ApplyServiceLayersAsync(
        ServiceEditExecution execution, IFeatureWriterTransaction transaction)
    {
        var call = execution.Call;
        var results = new FeatureEditResult[execution.Edits.Length];
        for (var index = 0; index < execution.Edits.Length; index++)
        {
            var edit = execution.Edits[index];
            var outbox = await ResolveServiceOutboxAsync(edit).ConfigureAwait(false);
            using var scope = FeatureMutationOutboxScope.BeginIfNotNull(outbox);
            results[index] = await transaction.ApplyEditsAsync(edit.Call.Layer.StorageLayerId,
                edit.Batch, call.Context.CancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    private async Task PublishServiceChangesAsync(ServiceEditExecution execution, FeatureEditResult[] results)
    {
        for (var index = 0; index < results.Length; index++)
        {
            var edit = execution.Edits[index];
            await PublishFeatureChangeEventsAsync(execution.Call.Request.ServiceId, edit.Call.Request.LayerId,
                edit.Call.Layer, edit.RequestBatch, results[index], execution.Call.Context).ConfigureAwait(false);
        }
    }

    private async Task<IFeatureWriterTransaction> BeginServiceTransactionAsync(ServerCallContext context)
    {
        try
        {
            return await _featureWriter.BeginTransactionAsync(IsolationLevel.RepeatableRead, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The storage provider does not support atomic multi-layer edits."));
        }
    }

    private Task<FeatureMutationOutboxScopeData?> ResolveServiceOutboxAsync(PreparedGrpcEdit edit)
        => _mutationEventService.ResolveOutboxScopeAsync(edit.Call.Context.GetHttpContext(),
            edit.Call.Request.LayerId, HonuaTelemetry.Protocols.Grpc,
            serviceId: edit.Call.Request.ServiceId, serviceProtocol: GrpcProtocolName,
            layerSrid: edit.Call.Layer.SpatialReference.ToSrid(),
            perOperationGeometryChanged: BuildPerOperationGeometryChanged(edit.RequestBatch),
            cancellationToken: edit.Call.Context.CancellationToken);

    private async Task<FeatureWriterTransactionCommitOutcome> CommitServiceEditsAsync(
        IFeatureWriterTransaction transaction, ServiceEditExecution execution,
        Proto.ApplyServiceEditsResponse response)
    {
        var rolledBack = response.Error is not null;
        var keyed = execution.Keyed;
        if (keyed is null)
        {
            return rolledBack ? FeatureWriterTransactionCommitOutcome.Committed
                : await transaction.CommitAsync(execution.Call.Context.CancellationToken).ConfigureAwait(false);
        }
        var receipt = new FeatureWriterTransactionReceipt
        {
            Scope = keyed.Scope,
            ExpiresAt = execution.Call.Receipts.ResponseExpiresAt,
            Payload = GrpcServiceEditsReceipts.EncodeReceipt(response, execution.Fingerprint)
        };
        try
        {
            if (!rolledBack)
            {
                return await transaction.CommitAsync(receipt, execution.Call.Context.CancellationToken).ConfigureAwait(false);
            }
            await using var receiptTransaction = await BeginServiceTransactionAsync(execution.Call.Context).ConfigureAwait(false);
            return await receiptTransaction.CommitAsync(receipt, execution.Call.Context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            keyed.RetainAmbiguous();
            throw;
        }
    }

    private static async Task<Proto.ApplyServiceEditsResponse> ReplayServiceReceiptAsync(ServiceEditExecution execution)
    {
        if (execution.Keyed is not { } keyed)
        {
            throw OutcomeMayHaveCommitted();
        }
        keyed.RetainAmbiguous();
        var recorded = await keyed.ReadCommittedAsync(CancellationToken.None).ConfigureAwait(false);
        if (recorded.Response is null || recorded.OutcomeUnknown)
        {
            throw OutcomeMayHaveCommitted();
        }
        EnsureServiceFingerprint(recorded.RequestFingerprint, execution.Fingerprint);
        keyed.Complete(recorded.Response, execution.Fingerprint);
        return FilterServiceGlobalIds(execution.Edits, recorded.Response);
    }

    private static Proto.ApplyServiceEditsResponse BuildServiceEditResponse(
        PreparedGrpcEdit[] edits, ServiceEditResults results)
    {
        var response = new Proto.ApplyServiceEditsResponse();
        for (var index = 0; index < edits.Length; index++)
        {
            response.Results.Add(new Proto.ServiceLayerEditResults
            {
                LayerId = edits[index].Call.Request.LayerId,
                Results = StampResultGlobalIds(edits[index].GlobalIds,
                    GrpcConversionHelpers.ToProtoApplyEditsResponse(results.Results[index]))
            });
        }
        if (results.Outcome == ServiceEditOutcome.RolledBack)
        {
            response.Error = new Proto.ErrorDetail
            {
                Code = 1000, Message = "Service edits rolled back.", Category = Proto.ErrorCategory.Execution
            };
        }
        return response;
    }

    private enum ServiceEditOutcome
    {
        Committed,
        RolledBack
    }

    private sealed record ServiceEditResults(FeatureEditResult[] Results, ServiceEditOutcome Outcome);
}
