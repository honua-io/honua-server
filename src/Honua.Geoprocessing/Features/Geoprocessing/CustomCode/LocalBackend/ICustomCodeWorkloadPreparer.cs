// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// The validated custom-code inputs the local backend reads off an admitted job's spec parameters.
/// Every field here was already gated at submit time by <c>CustomCodeSubmitValidator</c> (HTTPS +
/// repo allowlist, full 40-hex SHA pin, entrypoint shape, relative-only manifest); the backend
/// re-treats them as untrusted for defense in depth.
/// </summary>
/// <param name="Runtime">The <c>customcode.runtime</c> selector (<c>python</c> / <c>dotnet</c>).</param>
/// <param name="RepoUrl">The allowlisted absolute HTTPS repository URL.</param>
/// <param name="CommitSha">The pinned full 40-hex commit SHA.</param>
/// <param name="Entrypoint">The validated entrypoint (<c>module.path:function</c> for python).</param>
/// <param name="DepsManifest">The repo-relative dependency manifest path.</param>
internal readonly record struct CustomCodeJobInputs(
    string Runtime,
    string RepoUrl,
    string CommitSha,
    string Entrypoint,
    string DepsManifest);

/// <summary>
/// Materializes a pinned custom-code checkout into a contained directory and resolves how to launch
/// its entrypoint. This is a seam so the backend's OS-sandbox controls can be exercised without a git
/// remote or a language runtime: production wires the git + interpreter implementation, tests supply
/// a fake that drops a local script.
/// </summary>
internal interface ICustomCodeWorkloadPreparer
{
    /// <summary>
    /// Prepares the workload for a job by materializing its pinned commit under
    /// <paramref name="checkoutDirectory"/> (which the caller guarantees is a fresh directory
    /// contained in the job's single-use scratch root) and returning the executable + argv to launch
    /// under the sandbox.
    /// </summary>
    /// <param name="inputs">The validated custom-code inputs.</param>
    /// <param name="checkoutDirectory">A fresh, contained directory the checkout is written into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved launch spec.</returns>
    /// <exception cref="CustomCodeWorkloadPreparationException">
    /// Thrown (fail-closed) when the checkout cannot be materialized or the runtime is unsupported.
    /// </exception>
    ValueTask<SandboxLaunchSpec> PrepareAsync(
        CustomCodeJobInputs inputs,
        string checkoutDirectory,
        CancellationToken cancellationToken);
}

/// <summary>Raised when a custom-code workload cannot be prepared; the backend fails the job closed.</summary>
internal sealed class CustomCodeWorkloadPreparationException(string message) : Exception(message);
