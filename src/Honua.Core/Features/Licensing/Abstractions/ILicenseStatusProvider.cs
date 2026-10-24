// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Licensing.Abstractions;

/// <summary>
/// Provides current license status and entitlement information.
/// </summary>
public interface ILicenseStatusProvider
{
    /// <summary>
    /// Gets the current license status including edition, validity, and expiry.
    /// </summary>
    /// <returns>Current license status</returns>
    LicenseStatus GetCurrentStatus();

    /// <summary>
    /// Attempts to upload and apply a new license file.
    /// </summary>
    /// <param name="licenseStream">License file stream</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the upload operation</returns>
    Task<LicenseUploadResult> UploadLicenseAsync(Stream licenseStream, CancellationToken cancellationToken = default);
}
