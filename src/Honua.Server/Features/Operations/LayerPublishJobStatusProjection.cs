// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Domain;
using InfrastructureStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Features.Operations;

// Projects a background layer-publish job onto the operation status of the handle that queued it.
// Executors that add their own readback edit Details and ResourceIds before applying the view.
internal static class LayerPublishJobStatusProjection
{
    internal sealed record JobView(Dictionary<string, string> Details, Dictionary<string, string> ResourceIds);

    internal static JobView Describe(OperationHandle handle, LayerPublishJobProgress progress)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jobId"] = progress.OperationId,
            ["rowsMaterialized"] = progress.RowsMaterialized.ToString(CultureInfo.InvariantCulture),
            ["rowsTotal"] = progress.RowsTotal.ToString(CultureInfo.InvariantCulture),
            ["status"] = progress.Status.ToString(),
        };
        var resourceIds = new Dictionary<string, string>(handle.ResourceIds, StringComparer.Ordinal);
        if (progress.LayerId is int layerId)
        {
            var layerText = layerId.ToString(CultureInfo.InvariantCulture);
            details["storageLayerId"] = layerText;
            resourceIds["storageLayerId"] = layerText;
        }

        return new JobView(details, resourceIds);
    }

    internal static OperationStatus Apply(OperationStatus current, LayerPublishJobProgress progress, JobView view)
        => current with
        {
            Status = MapStatus(progress.Status),
            MetadataRevision = progress.MetadataRevision ?? current.MetadataRevision,
            UpdatedAt = progress.CompletedAt ?? current.UpdatedAt,
            Reason = progress.ErrorMessage ?? current.Reason,
            ResourceIds = view.ResourceIds,
            Result = new OperationResultSummary
            {
                Summary = progress.Status == InfrastructureStatus.Completed && progress.LayerId is int published
                    ? $"Published storage layer {published.ToString(CultureInfo.InvariantCulture)}."
                    : $"Layer publish {progress.Status}.",
                Details = view.Details,
            },
        };

    internal static OperationHandleStatus MapStatus(InfrastructureStatus status)
        => status switch
        {
            InfrastructureStatus.Queued => OperationHandleStatus.Queued,
            InfrastructureStatus.Processing => OperationHandleStatus.Running,
            InfrastructureStatus.Completed => OperationHandleStatus.Completed,
            InfrastructureStatus.Failed => OperationHandleStatus.Failed,
            InfrastructureStatus.Cancelled => OperationHandleStatus.Cancelled,
            _ => OperationHandleStatus.Running,
        };
}
