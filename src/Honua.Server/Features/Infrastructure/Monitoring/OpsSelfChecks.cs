// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal interface IOpsSelfCheckSource
{
    DateTimeOffset? CertificateExpiresAt { get; }

    string CurrentVersion { get; }

    Task<IReadOnlyList<OpsIdentityProviderHealth>> ReadIdentityProvidersAsync(CancellationToken cancellationToken);

    Task<OpsPublishedRelease?> ReadLatestReleaseAsync(CancellationToken cancellationToken);
}

internal sealed record OpsIdentityProviderHealth(Guid ProviderId, bool IsReachable);

internal sealed record OpsPublishedRelease(string Version, Uri ReleaseNotes);

internal sealed class OpsSelfChecks(TimeProvider clock, IOpsSelfCheckSource source)
{
    internal const string CertificateRule = "key-ring-certificate-expiry";
    internal const string IdentityProviderRule = "idp-unreachable";
    internal const string ReleaseRule = "honua-release-available";
    internal const string CollectionRule = "self-check-unavailable";

    public Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = clock;
        _ = source;
        return Task.FromResult<IReadOnlyList<OpsFinding>>([]);
    }
}
