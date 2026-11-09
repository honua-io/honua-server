// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing;

internal static class GeoprocessingResultRetention
{
    public static bool HasExpired(ExecutionJobRecord job, DateTimeOffset now, TimeSpan retention)
        => job.Spec.Kind == ExecutionJobKind.Geoprocessing
            && job.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled
            && now - (job.CompletedAt ?? job.UpdatedAt) >= retention;
}
