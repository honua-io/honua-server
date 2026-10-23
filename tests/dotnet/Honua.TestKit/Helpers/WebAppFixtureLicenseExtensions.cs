// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.TestKit.Helpers;

public static class WebAppFixtureLicenseExtensions
{
    public static WebAppFixture WithTestLicense(
        this WebAppFixture fixture,
        HonuaEdition edition,
        LicenseValidationState validationState = LicenseValidationState.Valid,
        string[]? entitlements = null)
    {
        var license = new TestLicenseEntitlementService(edition, validationState, entitlements);
        return fixture
            .ReplaceService<ILicenseEntitlementService>(license)
            .ReplaceService<ILicenseStatusProvider>(license);
    }
}
