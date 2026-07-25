// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Licensing;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Adds output caching only for requests whose active license includes the paid
/// output-cache entitlement.
/// </summary>
internal static class LicensedOutputCacheApplicationBuilderExtensions
{
    private const string OutputCacheEntitlement = "caching.output-cache";

    /// <summary>
    /// Branches licensed requests through ASP.NET Core output caching and bypasses
    /// the middleware entirely for unlicensed requests.
    /// </summary>
    public static IApplicationBuilder UseLicensedOutputCache(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseWhen(
            static context => LicenseGate.IsEntitlementActive(
                context.RequestServices,
                OutputCacheEntitlement),
            static branch => branch.UseOutputCache());
    }
}
