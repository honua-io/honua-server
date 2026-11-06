// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Licensing.Abstractions;

public interface ILicenseStatusProvider
{
    LicenseStatus GetCurrentStatus();

    Task<LicenseUploadResult> UploadLicenseAsync(Stream licenseStream, CancellationToken cancellationToken = default);
}
