// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Import;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static class ExternalServiceUrlValidation
{
    public static async Task<string?> ValidateGeoservicesUrlAsync(
        string serviceUrl,
        string parameterName,
        CancellationToken cancellationToken = default)
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(serviceUrl, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            return null;
        }

        return (result.ErrorMessage ?? $"{parameterName} is invalid")
            .Replace("ServiceUrl", parameterName, StringComparison.Ordinal);
    }
}
