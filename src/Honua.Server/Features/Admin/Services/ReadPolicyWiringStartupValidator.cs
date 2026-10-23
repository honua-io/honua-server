// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Fails startup when a read-policy store is registered without the request-scoped source that
/// turns its policies into an enforced row filter or field-mask set. The feature providers take
/// those sources as optional services, so a missing registration would otherwise leave stored
/// policies unapplied without any error. Registered outside Development and Test only.
/// </summary>
internal sealed class ReadPolicyWiringStartupValidator(IServiceProviderIsService registrations) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(registrations);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Throws when a policy store is registered and its enforcement source is not.
    /// </summary>
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
