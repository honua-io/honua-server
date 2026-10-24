// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed class ApiKeyAuthenticationDependencies
{
    public ApiKeyAuthenticationDependencies(
        IOptions<ApiKeyAuthenticationOptions> authOptions,
        IConnectionSecretResolver? secretResolver = null,
        IAdminApiKeyStore? adminApiKeyStore = null)
    {
        Options = authOptions?.Value ?? throw new ArgumentNullException(nameof(authOptions));
        SecretResolver = secretResolver;
        AdminApiKeyStore = adminApiKeyStore;
    }

    public ApiKeyAuthenticationOptions Options { get; }
    public IConnectionSecretResolver? SecretResolver { get; }
    public IAdminApiKeyStore? AdminApiKeyStore { get; }
}
