// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Migration;

/// <summary>
/// Shared handling of request-supplied secret references for the import endpoints and workers.
/// Every reference goes through <see cref="IRequestSecretReferenceResolver"/>; when that service is
/// not registered the reference is refused.
/// </summary>
internal static class RequestSecretReferenceValidation
{
    /// <summary>
    /// Returns a client-safe validation message when <paramref name="secretReference"/> is present
    /// and not permitted, otherwise <see langword="null"/>. Never resolves the reference.
    /// </summary>
    public static string? Validate(IServiceProvider serviceProvider, string? secretReference, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return null;
        }

        var resolver = serviceProvider.GetService<IRequestSecretReferenceResolver>();
        if (resolver is null)
        {
            return $"{fieldName}: {RequestSecretReferenceException.ClientSafeMessage}";
        }

        var decision = resolver.Evaluate(secretReference);
        return decision.IsPermitted ? null : $"{fieldName}: {decision.Reason}";
    }

    /// <summary>
    /// Resolves a permitted reference. Refusals and resolution failures surface as a
    /// <see cref="SecretNotFoundException"/> whose message names only the kind of secret.
    /// </summary>
    public static async Task<string> ResolveRequiredAsync(
        IServiceProvider serviceProvider,
        string secretReference,
        string secretDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var resolver = serviceProvider.GetService<IRequestSecretReferenceResolver>()
            ?? throw new SecretNotFoundException(
                secretReference,
                $"{secretDescription} secret reference could not be resolved.");

        try
        {
            return await resolver.ResolveAsync(secretReference, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestSecretReferenceException)
        {
            throw new SecretNotFoundException(
                secretReference,
                $"{secretDescription} secret reference could not be resolved.");
        }
    }
}
