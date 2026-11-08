// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal static class ExecutionAdmissionIdentity
{
    // A tenant with no durable owner is still charged so a nameless token cannot skip the rate gate.
    public static string ResolveRatePrincipal(string? ownerId, string? partitionKey)
    {
        var owner = string.IsNullOrWhiteSpace(ownerId) ? null : ownerId.Trim();
        var tenant = string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey.Trim();
        if (owner is not null)
        {
            return tenant is null ? owner : $"tenant:{tenant}:{owner}";
        }

        if (tenant is not null)
        {
            return $"tenant:{tenant}:unbound";
        }

        throw new GeoprocessingAdmissionException(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Rate,
            "execution.admission.identity",
            "Geoprocessing admission requires a durable owner or a tenant partition.",
            retryAfterSeconds: 10);
    }
}
