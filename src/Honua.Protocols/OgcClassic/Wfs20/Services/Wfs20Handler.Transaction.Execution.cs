// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    internal sealed class WfsPreparedEditRequest
    {
        public required int LayerId { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public required ImmutableArray<Wfs20EditOperation> Operations { get; init; }
        public required ImmutableArray<bool> RequestGeometryChangedFlags { get; init; }
        public bool RollbackOnFailure { get; init; }
        public IFeatureWriterTransaction? WriterTransaction { get; init; }
        public string? ServiceName { get; init; }
        public int? PublicLayerId { get; init; }
        public IReadOnlyDictionary<long, Feature>? StoredRows { get; init; }
    }

    internal async Task<FeatureEditResult> ExecutePreparedEditAsync(
        HttpContext context,
        WfsPreparedEditRequest request,
        CancellationToken cancellationToken)
    {
        var (result, _) = await ExecutePreparedEditCoreAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<(FeatureEditResult Result, (OgcProtocolEditGate.Target Target, FeatureEditBatch Batch, FeatureEditResult Result)? DeferredAfterHook)> ExecutePreparedEditCoreAsync(
        HttpContext context,
        WfsPreparedEditRequest request,
        CancellationToken cancellationToken)
    {
        var editBatch = await BuildValidatedEditBatchAsync(request, cancellationToken).ConfigureAwait(false);
        var gateTarget = new OgcProtocolEditGate.Target(context, request.Resource)
        {
            LayerId = request.LayerId,
            Reader = _featureReader,
            ServiceName = request.ServiceName,
            PublicLayerId = request.PublicLayerId,
            StoredRows = request.StoredRows,
            AlwaysLoadUpdateSnapshots = false,
            FailWholeBatchOnRejection = request.RollbackOnFailure,
        };
        var gate = await OgcProtocolEditGate.PrepareAsync(gateTarget, editBatch, cancellationToken)
            .ConfigureAwait(false);
        if (gate.Ended is { } ended)
        {
            return (ended, null);
        }

        var result = await WriteGatedEditBatchAsync(context, (request, gate), cancellationToken)
            .ConfigureAwait(false);
        if (request.WriterTransaction is null)
        {
            if (!result.WasRolledBack)
            {
                await OgcProtocolEditGate.RunAfterAsync(gateTarget, gate.Batch, result).ConfigureAwait(false);
            }

            return (result, null);
        }

        return (result, result.WasRolledBack ? null : (gateTarget, gate.Batch, result));
    }

    private async Task<FeatureEditBatch> BuildValidatedEditBatchAsync(
        WfsPreparedEditRequest request,
        CancellationToken cancellationToken)
    {
        var editAdapterResult = await _editParameterAdapter.ConvertAsync(
            new Wfs20EditRequest
            {
                Operations = request.Operations,
                RollbackOnFailure = request.RollbackOnFailure
            },
            cancellationToken).ConfigureAwait(false);
        if (!editAdapterResult.IsSuccess || editAdapterResult.EditRequest == null)
        {
            throw new InvalidOperationException(editAdapterResult.ErrorMessage ?? "Invalid WFS transaction.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, request.Resource);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, request.Resource);
        if (!editValidation.IsValid)
        {
            throw new InvalidOperationException(editValidation.ErrorMessage ?? "Invalid WFS transaction.");
        }

        return _editProcessor.ToFeatureEditBatch(optimizedEdit, request.Resource);
    }

    private async Task<FeatureEditResult> WriteGatedEditBatchAsync(
        HttpContext context,
        (WfsPreparedEditRequest Request, OgcProtocolEditGate.PreparedGate Gate) input,
        CancellationToken cancellationToken)
    {
        var (request, gate) = input;
        var batchToWrite = gate.Rejections.Count == 0
            ? gate.Batch
            : FeatureEditGate.WithoutRejected(gate.Batch, gate.Rejections);
        var effectiveGeometryChangedFlags = FilterGeometryChangedFlags(
            gate.Batch,
            request.RequestGeometryChangedFlags,
            gate.Rejections);
        var perOperationGeometryChanged = BuildPerOperationGeometryChanged(
            batchToWrite.Operations,
            effectiveGeometryChangedFlags);
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            request.LayerId,
            HonuaTelemetry.Protocols.Wfs20,
            serviceProtocol: MetadataV2ServiceProtocols.Wfs20,
            layerSrid: request.Resource.ReadSrid(),
            perOperationGeometryChanged: perOperationGeometryChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        var written = request.WriterTransaction is null
            ? await _featureWriter.ApplyEditsAsync(request.LayerId, batchToWrite, cancellationToken).ConfigureAwait(false)
            : await request.WriterTransaction.ApplyEditsAsync(request.LayerId, batchToWrite, cancellationToken).ConfigureAwait(false);
        return gate.Rejections.Count == 0
            ? written
            : FeatureEditGate.WithRejectedRows(written, gate.Batch, gate.Rejections);
    }

    private static async Task<FeatureEditResult> FinalizeMultiLayerTransactionAsync(
        IFeatureWriterTransaction writerTransaction,
        (FeatureEditResult AggregatedResult, List<(OgcProtocolEditGate.Target Target, FeatureEditBatch Batch, FeatureEditResult Result)> PendingAfterHooks) state,
        CancellationToken cancellationToken)
    {
        var (aggregatedResult, pendingAfterHooks) = state;
        if (!aggregatedResult.IsSuccess)
        {
            await writerTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return FeatureEditResult.Rollback(
                aggregatedResult.CreateResults,
                aggregatedResult.UpdateResults,
                aggregatedResult.DeleteResults);
        }

        var commitOutcome = await writerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (commitOutcome == FeatureWriterTransactionCommitOutcome.Committed)
        {
            foreach (var (target, batch, result) in pendingAfterHooks)
            {
                await OgcProtocolEditGate.RunAfterAsync(target, batch, result).ConfigureAwait(false);
            }

            return aggregatedResult;
        }

        const string errorMessage = "Transaction commit outcome is unknown.";
        return FeatureEditResult.FailureWithUnknownCommitOutcome(
            MarkCommitOutcomeUnknown(aggregatedResult.CreateResults, errorMessage),
            MarkCommitOutcomeUnknown(aggregatedResult.UpdateResults, errorMessage),
            MarkCommitOutcomeUnknown(aggregatedResult.DeleteResults, errorMessage));
    }

    private static ImmutableArray<EditOperationResult> MarkCommitOutcomeUnknown(
        ImmutableArray<EditOperationResult> results,
        string errorMessage)
        => results
            .Select(result => EditOperationResult.FailureWithUnknownCommitOutcome(
                errorMessage,
                objectId: result.ObjectId))
            .ToImmutableArray();

    private static ImmutableArray<bool> FilterGeometryChangedFlags(
        FeatureEditBatch batch,
        ImmutableArray<bool> flags,
        IReadOnlyList<FeatureEditHookRejection> rejections)
    {
        if (rejections.Count == 0 || flags.IsDefaultOrEmpty)
        {
            return flags;
        }

        var rejectedKeys = rejections.Select(static rejection => (rejection.Kind, rejection.Index)).ToHashSet();
        var rows = FeatureEditRows.Enumerate(batch);
        var builder = ImmutableArray.CreateBuilder<bool>(Math.Max(0, flags.Length - rejections.Count));
        for (var i = 0; i < rows.Count && i < flags.Length; i++)
        {
            if (!rejectedKeys.Contains((rows[i].Kind, rows[i].Index)))
            {
                builder.Add(flags[i]);
            }
        }

        return builder.ToImmutable();
    }

    private static Dictionary<long, Feature>? BuildDeleteStoredRows(
        ImmutableArray<PreparedTransactionOperation> operations)
    {
        Dictionary<long, Feature>? stored = null;
        foreach (var operation in operations)
        {
            if (operation.ActionKind == TransactionActionKind.Delete &&
                operation.DeleteSnapshot is { } snapshot &&
                operation.EditOperation.ObjectId is { } objectId)
            {
                stored ??= new Dictionary<long, Feature>();
                stored[objectId] = snapshot;
            }
        }

        return stored;
    }
}
