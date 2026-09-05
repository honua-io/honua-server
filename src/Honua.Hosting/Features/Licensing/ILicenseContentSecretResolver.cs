// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Cloud-neutral abstraction that resolves a license envelope from a secret
/// store reference (for example <c>aws:secretsmanager:&lt;arn&gt;</c>). The
/// concrete implementation lives in a provider project (e.g. <c>Honua.Aws</c>)
/// so the heavy cloud SDK surface stays out of the cloud-neutral
/// <c>Honua.Hosting</c> licensing pipeline. When no implementation is
/// registered, <see cref="FileBackedLicenseService"/> rejects paid-tier startup with a license-invalid error.
/// </summary>
public interface ILicenseContentSecretResolver
{
    /// <summary>
    /// Returns true when this resolver understands the supplied reference and
    /// can attempt to fetch it from its backing secret store.
    /// </summary>
    /// <param name="secretReference">The configured secret reference value.</param>
    /// <returns>True when the reference is supported by this resolver.</returns>
    bool CanResolve(string? secretReference);

    /// <summary>
    /// Fetches the license envelope content (UTF-8 JSON) addressed by the
    /// supplied secret reference. Returns <c>null</c> when the reference is
    /// unsupported or the secret could not be resolved; implementations must
    /// return null for a missing/unreachable secret so the licensing pipeline
    /// reports its documented license-invalid startup error.
    /// </summary>
    /// <param name="secretReference">The configured secret reference value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The license envelope JSON, or <c>null</c> when unavailable.</returns>
    Task<string?> ResolveLicenseContentAsync(
        string secretReference,
        CancellationToken cancellationToken = default);
}
