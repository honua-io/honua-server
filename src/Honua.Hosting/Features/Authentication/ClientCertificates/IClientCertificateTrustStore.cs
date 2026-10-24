// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal interface IClientCertificateTrustStore
{
    Task<IReadOnlyList<ClientCertificateTrustProfile>> ListProfilesAsync(
        string? environmentId = null,
        CancellationToken cancellationToken = default);

    Task<ClientCertificateTrustProfile?> GetProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task<ClientCertificateTrustProfile> UpsertProfileAsync(
        ClientCertificateTrustProfile profile,
        CancellationToken cancellationToken = default);

    Task<ClientCertificateTrustProfile?> DisableProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task<ClientCertificatePrincipalMapping?> UpsertMappingAsync(
        string profileId,
        ClientCertificatePrincipalMapping mapping,
        CancellationToken cancellationToken = default);

    Task<ClientCertificatePrincipalMapping?> DisableMappingAsync(
        string profileId,
        string mappingId,
        CancellationToken cancellationToken = default);

    Task<ClientCertificateRevocationEntry?> AddRevocationAsync(
        string profileId,
        ClientCertificateRevocationEntry revocation,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveRevocationAsync(
        string profileId,
        string revocationId,
        CancellationToken cancellationToken = default);
}
