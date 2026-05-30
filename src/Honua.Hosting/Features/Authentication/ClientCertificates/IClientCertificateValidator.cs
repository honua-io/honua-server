// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal interface IClientCertificateValidator
{
    Task<ClientCertificateValidationResult> ValidateAsync(
        X509Certificate2? certificate,
        CancellationToken cancellationToken = default);
}
