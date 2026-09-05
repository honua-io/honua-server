// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Styling;

namespace Honua.Server.Features.Operations;

internal static class StylePresetOperation
{
    public const string OperationId = "style.apply-preset";

    public static OperationDescriptor BuildDescriptor() => new()
    {
        OperationId = OperationId,
        ProviderId = ServicePublishOperation.ProviderId,
        Title = "Apply layer style preset",
        Description = "Associates a catalog style with a published layer and reconciles its metadata graph.",
        Category = "style",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.MutatesMetadata,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = true,
        },
        InputSchema = [Parameter("serviceId"), Parameter("layerId"), Parameter("styleId")],
        OutputSchema = [],
    };

    private static OperationParameterDescriptor Parameter(string name) => new()
    {
        Name = name,
        Title = name,
        Required = true,
        Schema = new WorkflowSchemaDefinition { Type = name == "layerId" ? WorkflowSchemaValueType.WholeNumber : WorkflowSchemaValueType.Text },
    };
}

// Resolve storage services only when selected, preserving protocol-only compositions.
internal sealed class StylePresetExecutor(IServiceProvider services) : IOperationExecutor
{
    public string OperationId => StylePresetOperation.OperationId;

    public async Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = await ResolveStorageLayerAsync(request, cancellationToken).ConfigureAwait(false);
        var catalog = services.GetRequiredService<IStyleCatalog>();
        var style = await catalog.GetStyleAsync(Required(request, "styleId"), cancellationToken).ConfigureAwait(false);
        _ = services.GetRequiredService<IMetadataV2StyleGraphSync>();
        return new OperationValidation
        {
            IsValid = style is not null,
            Status = style is null ? "invalid" : "valid",
            Messages = style is null ? ["The requested style preset does not exist."] : [],
        };
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var storageLayerId = await ResolveStorageLayerAsync(request, cancellationToken).ConfigureAwait(false);
        var catalog = services.GetRequiredService<IStyleCatalog>();
        var graphSync = services.GetRequiredService<IMetadataV2StyleGraphSync>();
        var styleId = Required(request, "styleId");
        if (!await catalog.AssociateLayerAsync(storageLayerId, styleId, 0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The style or layer no longer exists; no preset association was applied.");
        }

        string? warning = null;
        try
        {
            // The catalog commit is authoritative. As in OGC style editing, a
            // disconnected caller must not turn post-commit reconciliation into
            // a false failure receipt for a mutation that already happened.
            await graphSync.SyncLayerStylesAsync(storageLayerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            warning = "The style binding was applied, but metadata reconciliation is pending. Re-apply the preset to retry reconciliation.";
            var logger = services.GetService<ILogger<StylePresetExecutor>>();
            if (logger is not null)
            {
                LayerStyleLog.StandaloneStyleGraphSyncFailed(logger, styleId, exception);
            }
        }
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = warning,
            Result = new OperationResultSummary
            {
                Summary = "Applied layer style preset.",
                Details = warning is null ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["metadataReconciliationPending"] = bool.TrueString },
            },
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            Status = handle.Status,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            Result = handle.Result,
            Reason = handle.Reason,
        });

    private async Task<int> ResolveStorageLayerAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await services.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var serviceId = Required(request, "serviceId");
        if (!snapshot.Index.ServicesById.ContainsKey(serviceId))
        {
            throw new ArgumentException("The published service does not exist.", nameof(request));
        }

        if (!int.TryParse(Required(request, "layerId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            throw new ArgumentException("The published layer ID must be a 32-bit integer.", nameof(request));
        }

        var publication = snapshot.FindPublicationByLayerIndex(serviceId, layerId);
        if (publication is null || !snapshot.IsRoutable(publication))
        {
            throw new ArgumentException("The published layer is not routable.", nameof(request));
        }

        return snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(snapshot.ResolveResource(publication)!)
            ?? throw new ArgumentException("The published layer has no storage binding.", nameof(request));
    }

    private static string Required(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required operation parameter '{name}' is missing.");
}
