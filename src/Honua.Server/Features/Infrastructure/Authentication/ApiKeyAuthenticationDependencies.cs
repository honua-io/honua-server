// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal sealed class ApiKeyAuthenticationDependencies
{
    public ApiKeyAuthenticationDependencies(
        IOptions<ApiKeyAuthenticationOptions> authOptions,
        IConnectionSecretResolver? secretResolver = null)
    {
        Options = authOptions?.Value ?? throw new ArgumentNullException(nameof(authOptions));
        SecretResolver = secretResolver;
    }

    public ApiKeyAuthenticationOptions Options { get; }
    public IConnectionSecretResolver? SecretResolver { get; }
}
