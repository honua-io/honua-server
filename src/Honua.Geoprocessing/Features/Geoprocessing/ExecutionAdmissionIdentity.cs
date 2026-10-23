// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Builds the rate-limit principal for a geoprocessing submission from the durable owner
/// and tenant partition, not from a display name.
/// </summary>
internal static class ExecutionAdmissionIdentity
{
    /// <summary>
    /// Returns the principal id charged by <see cref="ExecutionAdmissionEvaluator"/>.
    /// A durable owner is tenant-scoped when a partition is present. A tenant without a
    /// durable owner is still charged, so a name-less token cannot skip the rate gate.
    /// Both missing fails closed.
    /// </summary>
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
