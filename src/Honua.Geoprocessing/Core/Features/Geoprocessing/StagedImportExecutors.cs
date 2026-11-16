// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

internal sealed class StagedImportPreviewExecutor(IServiceProvider services) : IOperationExecutor
{
    public string OperationId => StagedImportOperation.PreviewOperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var command = StagedImportOperation.ParseCommand(request, tenantId, actorId);
        var principal = await StagedImportOperation.AuthorizedPrincipalAsync(services, cancellationToken).ConfigureAwait(false);
        var preview = await ServerJobArtifactImporter.PreviewAsync(command,
            StagedImportOperation.ImportContext(services, principal), cancellationToken).ConfigureAwait(false);
        var binding = await new StagedImportPreviewBindings(services).CreateAsync(context, preview, cancellationToken).ConfigureAwait(false);
        return StagedImportHandles.Completed(services, context, new StagedImportResult(OperationId, "Previewed staged dataset import.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StagedImportOperation.PreviewFingerprintParameter] = binding.Identity,
                ["schema"] = command.Schema,
                ["stagingTable"] = command.Table,
                ["contentSha256"] = preview.ContentSha256,
                ["featureCount"] = preview.FeatureCount.ToString(CultureInfo.InvariantCulture),
                ["sourceSrid"] = preview.SourceSrid.ToString(CultureInfo.InvariantCulture),
                ["targetSrid"] = command.TargetSrid.ToString(CultureInfo.InvariantCulture),
                ["crsState"] = preview.CrsState,
                ["expiresAt"] = binding.Pin.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                ["approvalWindowMinutes"] = StagedImportOperation.ApprovalWindow.TotalMinutes.ToString(CultureInfo.InvariantCulture),
            }));
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(StagedImportHandles.Status(handle));
}

internal sealed class StagedImportExecutor(IServiceProvider services) : IOperationExecutor, IOperationRequestPreparer
{
    public string OperationId => StagedImportOperation.ImportOperationId;

    public async Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var command = StagedImportOperation.ParseCommand(request, tenantId, actorId);
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            // Approved replay keeps the sealed binding; freshness is re-checked at validation and import.
            _ = StagedImportPin.Read(request);
            return request;
        }

        if (StagedImportPin.HasAny(request))
        {
            throw new GeoprocessingValidationException("The preview binding is server-set; do not supply pinned parameters.");
        }

        var principal = await StagedImportOperation.AuthorizedPrincipalAsync(services, cancellationToken).ConfigureAwait(false);
        var identity = StagedImportOperation.Required(request, StagedImportOperation.PreviewFingerprintParameter);
        var pin = await new StagedImportPreviewBindings(services).ReadAsync(context, identity, cancellationToken).ConfigureAwait(false);
        var preview = await ServerJobArtifactImporter.PreviewAsync(command,
            StagedImportOperation.ImportContext(services, principal), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ServerJobArtifactImporter.PreviewBindingFingerprint(pin.Preview),
            ServerJobArtifactImporter.PreviewBindingFingerprint(preview), StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException(
                "The source changed since it was previewed; preview it again before proposing the import.");
        }

        var parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal);
        foreach (var (key, value) in pin.Parameters())
        {
            parameters[key] = value;
        }
        return request with { Parameters = parameters };
    }

    // Validation without a prepared binding (for example the standalone validate endpoint) reports
    // invalid instead of failing: only a proposal or approved replay carries the sealed pin.
    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        string? problem;
        var pin = StagedImportPin.TryRead(request);
        if (pin is null)
        {
            problem = "Propose this import with a previewFingerprint from dataset.stage.preview; it has no approved preview binding.";
        }
        else
        {
            problem = pin.IsExpired(services.GetService<TimeProvider>() ?? TimeProvider.System)
                ? "The approval window for this staged import has passed; preview and propose it again."
                : null;
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
        var pin = StagedImportPin.Read(request);
        if (pin.IsExpired(services.GetService<TimeProvider>() ?? TimeProvider.System))
        {
            throw new GeoprocessingPreconditionFailedException("The approval window for this staged import has passed.");
        }

        var command = StagedImportOperation.ParseCommand(request, tenantId, actorId) with { StagePreview = pin.Preview };
        var principal = await StagedImportOperation.AuthorizedPrincipalAsync(services, cancellationToken).ConfigureAwait(false);
        var receipt = await ServerJobArtifactImporter.ImportAsync(command, principal,
            services.GetRequiredService<IGeoprocessingJobService>(), services.GetRequiredService<IFileImportService>(),
            services.GetRequiredService<IServerJobArtifactImportLedger>(), services, cancellationToken).ConfigureAwait(false);
        return StagedImportHandles.Completed(services, context, new StagedImportResult(OperationId, "Imported the previewed dataset into fresh staging.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["intentId"] = receipt.IntentId,
                ["schema"] = receipt.Schema,
                ["stagingTable"] = command.Table,
                ["physicalTable"] = receipt.PhysicalTable,
                ["featureCount"] = receipt.FeatureCount.ToString(CultureInfo.InvariantCulture),
                ["contentSha256"] = receipt.ContentSha256,
            }));
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(StagedImportHandles.Status(handle));
}

internal sealed record StagedImportResult(string OperationId, string Summary, IReadOnlyDictionary<string, string> Details);

internal static class StagedImportHandles
{
    public static OperationHandle Completed(IServiceProvider services, OperationPolicyContext context, StagedImportResult result)
    {
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = result.OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary { Summary = result.Summary, Details = result.Details },
        };
    }

    public static OperationStatus Status(OperationHandle handle) => new()
    {
        OperationInstanceId = handle.OperationInstanceId,
        OperationId = handle.OperationId,
        CorrelationId = handle.CorrelationId,
        Status = handle.Status,
        CreatedAt = handle.CreatedAt,
        UpdatedAt = handle.UpdatedAt,
        Result = handle.Result,
        Reason = handle.Reason,
    };
}
