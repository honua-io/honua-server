// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Models;

namespace Honua.Geoprocessing;

internal static class GeoprocessingProblemDetailsHelpers
{
    public static IResult StoreUnavailable(
        HttpContext context,
        GeoprocessingStoreUnavailableException exception)
        => exception.HasDependencyReceipt
            ? ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                exception.Message,
                exception.MissingDependency,
                exception.Remediation!,
                exception.RemediationRef!,
                exception.CapabilityId,
                exception.ErrorCode,
                exception.MissingEntitlement)
            : ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                exception.Message);
}
