// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

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
        await catalog.AssociateLayerAsync(storageLayerId, Required(request, "styleId"), 0, cancellationToken)
            .ConfigureAwait(false);
        await graphSync.SyncLayerStylesAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary { Summary = "Applied layer style preset." },
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

        var publication = snapshot.FindPublicationByLayerIndex(serviceId,
            int.Parse(Required(request, "layerId"), CultureInfo.InvariantCulture));
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
