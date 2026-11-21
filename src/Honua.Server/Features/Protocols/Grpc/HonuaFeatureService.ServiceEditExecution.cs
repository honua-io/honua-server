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
    // Intentional divergence from single-layer ApplyEdits: under rollback_on_failure, a hook or
    // attribute-rule rejection rolls back the multi-layer transaction and returns per-layer results
    // with code 1000 (recorded in any durable receipt) instead of throwing INVALID_ARGUMENT.
    private async Task<Proto.ApplyServiceEditsResponse> ExecuteServiceEditsAsync(ServiceEditExecution execution)
    {
        var run = await RunServiceTransactionAsync(execution).ConfigureAwait(false);
        var commitOutcome = run.CommitOutcome;
        // Persist rolled-back keyed receipts only after the outer transaction has released its connection.
        if (run.Outcome == ServiceEditOutcome.RolledBack && execution.Keyed is not null)
        {
            commitOutcome = await CommitRolledBackReceiptAsync(execution, run.Response).ConfigureAwait(false);
        }
        if (commitOutcome != FeatureWriterTransactionCommitOutcome.Committed)
        {
            return await ReplayServiceReceiptAsync(execution).ConfigureAwait(false);
        }
        execution.Keyed?.Complete(run.Response, execution.Fingerprint);
        if (run.Outcome == ServiceEditOutcome.Committed)
        {
            await FinalizeCommittedServiceEditsAsync(execution, run.Written, run.Reported).ConfigureAwait(false);
        }
        return run.Response;
    }

    private async Task<ServiceTransactionRun> RunServiceTransactionAsync(ServiceEditExecution execution)
    {
        var call = execution.Call;
        await using var transaction = await BeginServiceTransactionAsync(call.Context).ConfigureAwait(false);
        if (execution.Keyed is not null && !transaction.SupportsDurableReceipts)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "The transaction provider does not support durable service edit receipts."));
        }
        var written = await ApplyServiceLayersAsync(execution, transaction).ConfigureAwait(false);
        var reported = written.Select((result, index) => execution.Edits[index].Gate!.Merge(result)).ToArray();
        var rolledBack = call.Request.RollbackOnFailure && reported.Any(result => result.HasErrors);
        if (rolledBack)
        {
            await RollbackServiceResultsAsync(transaction, reported).ConfigureAwait(false);
        }
        var outcome = rolledBack ? ServiceEditOutcome.RolledBack : ServiceEditOutcome.Committed;
        var response = BuildServiceEditResponse(execution.Edits, new ServiceEditResults(reported, outcome));
        var commitOutcome = rolledBack
            ? FeatureWriterTransactionCommitOutcome.Committed
            : await CommitActiveTransactionAsync(transaction, execution, response).ConfigureAwait(false);
        return new ServiceTransactionRun
        {
            Written = written,
            Reported = reported,
            Outcome = outcome,
            Response = response,
            CommitOutcome = commitOutcome
        };
    }

    private sealed record ServiceTransactionRun
    {
        public required FeatureEditResult[] Written { get; init; }
        public required FeatureEditResult[] Reported { get; init; }
        public required ServiceEditOutcome Outcome { get; init; }
        public required Proto.ApplyServiceEditsResponse Response { get; init; }
        public required FeatureWriterTransactionCommitOutcome CommitOutcome { get; init; }
    }

    private static async Task RollbackServiceResultsAsync(
        IFeatureWriterTransaction transaction, FeatureEditResult[] reported)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < reported.Length; index++)
        {
            var result = reported[index];
            reported[index] = FeatureEditResult.Rollback(result.CreateResults, result.UpdateResults, result.DeleteResults);
        }
    }

    private async Task FinalizeCommittedServiceEditsAsync(
        ServiceEditExecution execution, FeatureEditResult[] written, FeatureEditResult[] reported)
    {
        try
        {
            await PublishServiceChangesAsync(execution, reported).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(execution.Edits.Select((edit, index) => edit.Gate!.RunAfterAsync(written[index])))
                .ConfigureAwait(false);
        }
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
                edit.Gate!.Written, call.Context.CancellationToken).ConfigureAwait(false);
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
            perOperationGeometryChanged: BuildPerOperationGeometryChanged(edit.Gate!.Without(edit.RequestBatch)),
            cancellationToken: edit.Call.Context.CancellationToken);

    private static async Task<FeatureWriterTransactionCommitOutcome> CommitActiveTransactionAsync(
        IFeatureWriterTransaction transaction, ServiceEditExecution execution,
        Proto.ApplyServiceEditsResponse response)
    {
        var keyed = execution.Keyed;
        if (keyed is null)
        {
            return await transaction.CommitAsync(execution.Call.Context.CancellationToken).ConfigureAwait(false);
        }
        var receipt = BuildServiceReceipt(execution, response);
        try
        {
            return await transaction.CommitAsync(receipt, execution.Call.Context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            keyed.RetainAmbiguous();
            throw;
        }
    }

    private async Task<FeatureWriterTransactionCommitOutcome> CommitRolledBackReceiptAsync(
        ServiceEditExecution execution, Proto.ApplyServiceEditsResponse response)
    {
        var keyed = execution.Keyed!;
        var receipt = BuildServiceReceipt(execution, response);
        try
        {
            await using var receiptTransaction = await BeginServiceTransactionAsync(execution.Call.Context).ConfigureAwait(false);
            return await receiptTransaction.CommitAsync(receipt, execution.Call.Context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            keyed.RetainAmbiguous();
            throw;
        }
    }

    private static FeatureWriterTransactionReceipt BuildServiceReceipt(
        ServiceEditExecution execution, Proto.ApplyServiceEditsResponse response)
        => new()
        {
            Scope = execution.Keyed!.Scope,
            ExpiresAt = execution.Call.Receipts.ResponseExpiresAt,
            Payload = GrpcServiceEditsReceipts.EncodeReceipt(response, execution.Fingerprint)
        };

    private static async Task<Proto.ApplyServiceEditsResponse> ReplayServiceReceiptAsync(ServiceEditExecution execution)
    {
        if (execution.Keyed is not { } keyed)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The service edit commit outcome is unknown."));
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
                Code = 1000,
                Message = "Service edits rolled back.",
                Category = Proto.ErrorCategory.Execution
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
