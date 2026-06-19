// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Resolves a <c>Licensing:LicenseContentSecretRef</c> reference (e.g.
/// <c>azure:keyvault:https://&lt;vault&gt;.vault.azure.net/&lt;secret&gt;</c>) into the signed license
/// envelope JSON, so the envelope can be delivered from a cloud secret store at startup rather than
/// baked into the image or supplied in clear text. The resolved value is treated as the inline
/// license envelope.
/// </summary>
/// <remarks>
/// <para>
/// <b>PROVISIONAL (draft).</b> The canonical owner of this abstraction is honua-server#1742 (the
/// AWS Secrets Manager license resolver), which has no PR on trunk yet. This interface is defined
/// here so the Azure Key Vault resolver (#1745) can be drafted and wired end-to-end; when #1742
/// lands, reconcile this with the canonical interface (signature/namespace) — most likely delete
/// this draft copy and consume #1742's. Kept SEPARATE from
/// <c>Honua.Core.Features.Security.Abstractions.IConnectionSecretResolver</c> (which resolves
/// database connection-string secrets) — do not conflate the two seams.
/// </para>
/// <para>
/// Implementations live in the per-cloud assemblies (Azure Key Vault in <c>Honua.Azure</c>; AWS
/// Secrets Manager in <c>Honua.Aws</c>, per #1742). The license loader treats this resolver as
/// OPTIONAL and FAIL-SAFE: any resolver failure must fall back to Community licensing rather than
/// crash the host.
/// </para>
/// </remarks>
public interface ILicenseContentSecretResolver
{
    /// <summary>
    /// Returns true when this resolver recognizes the supplied license-content secret reference
    /// (e.g. an <c>azure:keyvault:</c> prefix for the Azure Key Vault resolver).
    /// </summary>
    bool CanResolve(string secretRef);

    /// <summary>
    /// Resolves the secret reference to the signed license envelope JSON. Throws on any failure
    /// (network, auth, malformed reference); the license loader catches and fails safe to Community.
    /// </summary>
    Task<string> ResolveLicenseContentAsync(string secretRef, CancellationToken cancellationToken = default);
}
