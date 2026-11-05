// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Server.Features.Admin.Services;

internal sealed class ReadPolicyWiringStartupValidator(IServiceProviderIsService registrations) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(registrations);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void Validate(IServiceProviderIsService registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        if (registrations.IsService(typeof(IRlsPolicyStore)) &&
            !registrations.IsService(typeof(IRowLevelSecurityFilterSource)))
        {
            throw new InvalidOperationException(
                "A row-level security policy store (IRlsPolicyStore) is registered, but the request-scoped " +
                "IRowLevelSecurityFilterSource is not, so stored row-level security policies would not be applied to reads. " +
                "Register IRowLevelSecurityFilterSource, or remove the policy store.");
        }

        if (registrations.IsService(typeof(IFieldMaskPolicyStore)) &&
            !registrations.IsService(typeof(IFieldMaskSource)))
        {
            throw new InvalidOperationException(
                "A field-mask policy store (IFieldMaskPolicyStore) is registered, but the request-scoped " +
                "IFieldMaskSource is not, so stored field-mask policies would not be applied to reads. " +
                "Register IFieldMaskSource, or remove the policy store.");
        }
    }
}
