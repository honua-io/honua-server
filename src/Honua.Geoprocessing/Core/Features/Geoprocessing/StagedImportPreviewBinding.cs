// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

// Each preview has its own durable identity. Re-previewing an unchanged source cannot renew an older binding.
internal sealed class StagedImportPreviewBindings(IServiceProvider services)
{
    internal sealed record Binding(string Identity, StagedImportPin Pin);

    private const string InstancePrefix = "stage-preview-";

    internal async Task<Binding> CreateAsync(
        OperationPolicyContext context, ServerJobArtifactStagePreview preview, CancellationToken cancellationToken)
    {
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var identity = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var pin = new StagedImportPin(preview, now + StagedImportOperation.ApprovalWindow);
        var handle = StagedImportHandles.Completed(services, context,
            new StagedImportResult(StagedImportOperation.PreviewOperationId, "Sealed staged import preview.",
                pin.Parameters().ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal))) with
        {
            OperationInstanceId = InstancePrefix + identity,
            TenantId = tenantId,
            ResourceIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["requester"] = actorId },
        };
        if (!await services.GetRequiredService<IOperationInstanceStore>().TryCreateAsync(handle, cancellationToken).ConfigureAwait(false))
        {
            throw new GeoprocessingPreconditionFailedException("The preview could not be persisted; preview it again.");
        }

        return new Binding(identity, pin);
    }

    internal async Task<StagedImportPin> ReadAsync(OperationPolicyContext context,
        string identity, CancellationToken cancellationToken)
    {
        var (tenantId, actorId) = StagedImportOperation.RequireScope(context);
        if (identity.Length != 64 || !identity.All(char.IsAsciiHexDigit))
        {
            throw Unavailable();
        }

        var handle = await services.GetRequiredService<IOperationInstanceStore>()
            .GetAsync(InstancePrefix + identity, cancellationToken).ConfigureAwait(false);
        if (handle is null || handle.OperationId != StagedImportOperation.PreviewOperationId
            || handle.Status != OperationHandleStatus.Completed || handle.TenantId != tenantId
            || !handle.ResourceIds.TryGetValue("requester", out var requester) || requester != actorId
            || handle.Result is null)
        {
            throw Unavailable();
        }

        var pin = StagedImportPin.Read(new OperationRequest
        {
            OperationId = StagedImportOperation.ImportOperationId,
            Parameters = handle.Result.Details.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
        });
        if (pin.IsExpired(services.GetService<TimeProvider>() ?? TimeProvider.System))
        {
            throw Unavailable();
        }

        return pin;
    }

    private static GeoprocessingPreconditionFailedException Unavailable()
        => new("The staged import preview is unavailable or expired for this requester; preview it again.");
}
