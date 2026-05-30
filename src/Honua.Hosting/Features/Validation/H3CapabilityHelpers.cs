// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Shared validation for H3 capability checks across FeatureServer and OGC endpoints.
/// </summary>
internal static class H3CapabilityHelpers
{
    /// <summary>
    /// Checks whether the h3-pg extension is available and returns an appropriate
    /// error result if not. Returns null when H3 is available.
    /// </summary>
    internal static async Task<IResult?> ValidateH3AvailabilityAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        var h3Checker = context.RequestServices.GetRequiredService<IH3CapabilityChecker>();
        var h3Available = await h3Checker.IsH3AvailableAsync(cancellationToken);
        if (h3Available == false)
        {
            return StandardErrorHelpers.CreateNotImplemented(context,
                H3AggregationQuery.CapabilityErrorTitle,
                [H3AggregationQuery.CapabilityErrorDetail]);
        }

        if (h3Available is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context,
                H3AggregationQuery.CapabilityCheckFailedDetail, retryAfterSeconds: 60);
        }

        return null;
    }
}
