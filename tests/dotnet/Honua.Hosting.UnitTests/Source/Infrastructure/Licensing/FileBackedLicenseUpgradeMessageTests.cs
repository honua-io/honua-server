// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Infrastructure.Licensing;

public sealed class FileBackedLicenseUpgradeMessageTests
{
    [UnitTest]
    public void CheckEntitlement_RepeatedInactiveFeature_ReusesCachedUpgradeMessage()
    {
        var options = Options.Create(new LicenseOptions { Edition = HonuaEdition.Community });
        using var service = new FileBackedLicenseService(
            options,
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance);

        var first = service.CheckEntitlement(FeatureCatalog.MultiTenancyKey);
        var second = service.CheckEntitlement(FeatureCatalog.MultiTenancyKey);

        Assert.False(first.IsActive);
        Assert.False(string.IsNullOrEmpty(first.UpgradeMessage));
        Assert.Same(first.UpgradeMessage, second.UpgradeMessage);
    }
}
