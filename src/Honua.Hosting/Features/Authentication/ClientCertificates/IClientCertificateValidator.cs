// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal interface IClientCertificateValidator
{
    Task<ClientCertificateValidationResult> ValidateAsync(
        X509Certificate2? certificate,
        CancellationToken cancellationToken = default);
}
