// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// License status provider that derives edition from AlertOptions configuration.
/// Returns a static community license until #338 provides license file parsing.
/// </summary>
internal sealed class ConfigurationLicenseStatusProvider : ILicenseStatusProvider
{
    private readonly IOptions<AlertOptions> _alertOptions;

    public ConfigurationLicenseStatusProvider(IOptions<AlertOptions> alertOptions)
    {
        _alertOptions = alertOptions;
    }

    /// <inheritdoc />
    public LicenseStatus GetCurrentStatus()
    {
        var edition = MapEdition(_alertOptions.Value.Edition);

        return new LicenseStatus(
            Edition: edition,
            IsValid: true,
            ExpiresAt: null,
            LicensedTo: null);
    }

    /// <inheritdoc />
    public Task<LicenseUploadResult> UploadLicenseAsync(Stream licenseStream, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LicenseUploadResult(false, "License upload is not yet supported. This feature will be available in a future release (#338)."));
    }

    private static HonuaEdition MapEdition(AlertEdition alertEdition) =>
        alertEdition switch
        {
            AlertEdition.Pro => HonuaEdition.Pro,
            AlertEdition.Enterprise => HonuaEdition.Enterprise,
            _ => HonuaEdition.Community,
        };
}
