// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

public interface ILicenseContentSecretResolver
{
    bool CanResolve(string? secretReference);

    // An implementation returns null for a missing or unreachable secret so startup reports the documented license state instead of a provider error.
    Task<string?> ResolveLicenseContentAsync(
        string secretReference,
        CancellationToken cancellationToken = default);
}
