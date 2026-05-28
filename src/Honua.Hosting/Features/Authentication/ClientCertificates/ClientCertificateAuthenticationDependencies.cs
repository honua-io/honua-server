// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateAuthenticationDependencies(
    ClientCertificateExtractor extractor,
    IClientCertificateValidator validator,
    IOptionsMonitor<ClientCertificateAuthenticationOptions> options)
{
    public ClientCertificateExtractor Extractor { get; } = extractor ?? throw new ArgumentNullException(nameof(extractor));

    public IClientCertificateValidator Validator { get; } = validator ?? throw new ArgumentNullException(nameof(validator));

    public IOptionsMonitor<ClientCertificateAuthenticationOptions> Options { get; } = options ?? throw new ArgumentNullException(nameof(options));
}
