// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Server.Features.Operations;

internal sealed class ServicePublicationPreviewExecutor(IServiceProvider services)
    : IOperationExecutor, IOperationRequestPreparer
{
    public string OperationId => ServicePublicationPreviewOperation.OperationId;

    public Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Parameters.ContainsKey(PublicationPreviewBindings.ReferenceKey) || context.ApprovedProposalId is not null)
        {
            throw new InvalidOperationException("Create a fresh publication preview without a reference or approval.");
        }
        return new DeferredServicePublishExecutor(services).PrepareAsync(request, context, cancellationToken);
    }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
        => new DeferredServicePublishExecutor(services).ValidateAsync(request, cancellationToken);

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (services.GetService<ILayerPublishingService>() is not ILayerPublicationPreviewReader reader)
        {
            throw new InvalidOperationException("Publication preview is unavailable.");
        }
        var resolver = services.GetRequiredService<ISecureConnectionResolver>();
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            throw new ArgumentException("Publication preview requires a saved connectionId.");
        }
        var connection = Guid.TryParse(request.ConnectionId, out var id)
            ? await resolver.ResolveConnectionStringAsync(id, cancellationToken).ConfigureAwait(false)
            : await resolver.ResolveConnectionStringAsync(request.ConnectionId, cancellationToken).ConfigureAwait(false);
        var preview = await reader.PreviewPublicationAsync(connection,
            ServicePublishExecutor.CreatePublishRequest(request), cancellationToken).ConfigureAwait(false);
        if (ServicePublishMetadataPin.Require(request) != preview.MetadataBase)
        {
            throw new InvalidOperationException("Publication metadata changed while previewing. Create a new preview.");
        }
        var binding = await services.GetRequiredService<PublicationPreviewBindings>()
            .CreateAsync(request, new PublicationPreviewIdentity(context, preview.MetadataBase, preview.SourceFingerprint), cancellationToken).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var details = BuildDetails(request, preview, binding);
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            TenantId = context.TenantId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            MetadataRevision = preview.MetadataBase.Revision,
            Result = new OperationResultSummary { Summary = "Previewed publication; no layer or route allocated.", Details = details }
        };
    }

    private static Dictionary<string, string> BuildDetails(OperationRequest request, LayerPublicationPreview preview,
        (string Reference, DateTimeOffset ExpiresAt) binding)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = request.Parameters["schema"]!,
            ["table"] = request.Parameters["table"]!,
            ["serviceName"] = preview.Service.Metadata.Name,
            ["layerName"] = request.Parameters["layerName"]!,
            ["metadataRevision"] = preview.MetadataBase.Revision.ToString(CultureInfo.InvariantCulture),
            ["metadataEtag"] = preview.MetadataBase.Etag ?? string.Empty,
            [PublicationPreviewBindings.ReferenceKey] = binding.Reference,
            ["expiresAt"] = binding.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
            ["layerIdentity"] = "unallocated",
            ["routesAreAllocated"] = "false",
            ["plannedService"] = JsonSerializer.Serialize(preview.Service, MetadataV2JsonContext.Default.MetadataV2Service),
            ["plannedResource"] = JsonSerializer.Serialize(preview.Resource, MetadataV2JsonContext.Default.MetadataV2Object),
            ["plannedStorageBinding"] = JsonSerializer.Serialize(preview.StorageBinding, MetadataV2JsonContext.Default.MetadataV2StorageBinding),
            ["plannedPublications"] = "[" + string.Join(",", preview.Publications.Select(publication =>
                JsonSerializer.Serialize(publication, MetadataV2JsonContext.Default.MetadataV2Layer))) + "]"
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
            Reason = handle.Reason,
            Result = handle.Result
        });
}
