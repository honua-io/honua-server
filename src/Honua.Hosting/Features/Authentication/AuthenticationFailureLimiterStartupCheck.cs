// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed class AuthenticationFailureLimiterStartupCheck(
    IOptions<AuthenticationFailureLimiterOptions> options,
    IConfiguration configuration,
    ILogger<AuthenticationFailureLimiterStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled && !configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
        {
            AuthenticationLog.AuthenticationSourceIsConnectionAddress(logger);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
