// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Licensing.Abstractions;

/// <summary>
/// Provides fast runtime access to the active license snapshot and entitlement decisions.
/// </summary>
public interface ILicenseEntitlementService
{
    /// <summary>
    /// Gets the immutable active license snapshot.
    /// </summary>
    /// <returns>The current license snapshot.</returns>
    LicenseSnapshot GetSnapshot();

    /// <summary>
    /// Checks whether a specific entitlement key is active.
    /// </summary>
    /// <param name="entitlementKey">Feature entitlement key from <see cref="FeatureCatalog"/>.</param>
    /// <returns>The entitlement decision for the current snapshot.</returns>
    LicenseEntitlementDecision CheckEntitlement(string entitlementKey);
}
