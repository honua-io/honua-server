// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
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
internal sealed class StylePresetExecutor(IServiceProvider services) : IOperationExecutor, IOperationRequestPreparer
{
    private static readonly ActivitySource ActivitySource = new("Honua", "1.0.0");

    public string OperationId => StylePresetOperation.OperationId;

    public async Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity(request, "prepare");
        try
        {
            var target = await ResolveTargetAsync(request, cancellationToken).ConfigureAwait(false);
            if (StylePresetTargetPin.HasPin(request) || !string.IsNullOrWhiteSpace(context.ApprovedProposalId))
            {
                target.Verify(request, required: true);
                return request;
            }

            var parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal);
            foreach (var (key, value) in target.Parameters())
            {
                parameters[key] = value;
            }
            return request with { Parameters = parameters };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailure(activity, exception);
            throw;
        }
    }

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
        using var activity = StartActivity(request, "execute");
        try
        {
            var storageLayerId = await ResolveStorageLayerAsync(request, cancellationToken, requirePin: true).ConfigureAwait(false);
            activity?.SetTag("storage.layer.id", storageLayerId);
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
                RecordFailure(activity, exception);
                warning = "The style binding was applied, but metadata reconciliation is pending. Re-apply the preset to retry reconciliation.";
                var logger = services.GetService<ILogger<StylePresetExecutor>>();
                if (logger is not null)
                {
                    LayerStyleLog.StandaloneStyleGraphSyncFailed(logger, styleId, exception);
                }
            }
            activity?.SetTag("operation.result", warning is null ? "applied" : "reconciliation-pending");
            if (warning is null)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
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
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailure(activity, exception);
            throw;
        }
    }

    private static Activity? StartActivity(OperationRequest request, string phase)
    {
        var activity = ActivitySource.StartActivity($"style.apply-preset.{phase}");
        activity?.SetTag("operation.id", StylePresetOperation.OperationId);
        foreach (var (parameter, tag) in new[] { ("serviceId", "service.id"), ("layerId", "layer.id"), ("styleId", "style.id") })
        {
            if (request.Parameters.TryGetValue(parameter, out var value))
            {
                activity?.SetTag(tag, value);
            }
        }
        return activity;
    }

    private static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetTag("operation.result", "failed");
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, "Style preset operation failed.");
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

    private async Task<int> ResolveStorageLayerAsync(OperationRequest request, CancellationToken cancellationToken,
        bool requirePin = false)
    {
        var target = await ResolveTargetAsync(request, cancellationToken).ConfigureAwait(false);
        target.Verify(request, requirePin);
        return target.StorageLayerId;
    }

    private async Task<StylePresetTargetPin> ResolveTargetAsync(OperationRequest request, CancellationToken cancellationToken)
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

        var resource = snapshot.ResolveResource(publication)!;
        var binding = snapshot.ResolveStorageBinding(publication);
        var storageLayerId = binding?.StorageLayerId
            ?? throw new ArgumentException("The published layer has no storage binding.", nameof(request));
        return new StylePresetTargetPin(publication.Metadata.Id, resource.Metadata.Id,
            binding!.Metadata.Id, storageLayerId);
    }

    private static string Required(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required operation parameter '{name}' is missing.");
}


internal sealed record StylePresetTargetPin(string PublicationId, string ResourceId, string StorageBindingId, int StorageLayerId)
{
    private static readonly string[] PinKeys =
        ["expectedPublicationId", "expectedResourceId", "expectedStorageBindingId", "expectedStorageLayerId"];

    public Dictionary<string, string?> Parameters() => new(StringComparer.Ordinal)
    {
        [PinKeys[0]] = PublicationId,
        [PinKeys[1]] = ResourceId,
        [PinKeys[2]] = StorageBindingId,
        [PinKeys[3]] = StorageLayerId.ToString(CultureInfo.InvariantCulture),
    };

    public static bool HasPin(OperationRequest request) => PinKeys.Any(request.Parameters.ContainsKey);

    public static void RequirePin(OperationRequest request)
    {
        if (!PinKeys.All(request.Parameters.ContainsKey))
        {
            throw new ArgumentException("The style preset request is missing its approved target identity.", nameof(request));
        }
    }

    public void Verify(OperationRequest request, bool required)
    {
        if (!required && !HasPin(request))
        {
            return;
        }
        RequirePin(request);
        foreach (var (key, value) in Parameters())
        {
            if (!string.Equals(request.Parameters[key], value, StringComparison.Ordinal))
            {
                throw new ArgumentException("The style preset target changed; create a new approval request.", nameof(request));
            }
        }
    }
}
