// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
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

internal sealed class OpsSelfChecks(TimeProvider clock, IOpsSelfCheckSource source) : IDisposable
{
    internal const string CertificateRule = "key-ring-certificate-expiry";
    internal const string IdentityProviderRule = "idp-unreachable";
    internal const string ReleaseRule = "honua-release-available";
    internal const string CollectionRule = "self-check-unavailable";

    private readonly SemaphoreSlim _gate = new(1, 1);
    // ADR-0086 exception: these sampled health observations never authorize requests.
    // Persisted providers refresh before every five-minute probe; the configuration-independent
    // public release feed is sampled daily. Certificate lifetime is immutable and evaluated fresh.
    private DateTimeOffset? _providersReadAt;
    private DateTimeOffset? _releaseReadAt;
    private IReadOnlyList<OpsFinding> _providerFindings = [];
    private IReadOnlyList<OpsFinding> _releaseFindings = [];

    public async Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (_providersReadAt is null || now - _providersReadAt >= TimeSpan.FromMinutes(5))
            {
                _providerFindings = await CollectAsync(new(ReadProvidersAsync, "sign-in-providers", now), cancellationToken).ConfigureAwait(false);
                _providersReadAt = now;
            }
            if (_releaseReadAt is null || now - _releaseReadAt >= TimeSpan.FromDays(1))
            {
                _releaseFindings = await CollectAsync(new(ReadReleaseAsync, "public-release-feed", now), cancellationToken).ConfigureAwait(false);
                _releaseReadAt = now;
            }
            var findings = new List<OpsFinding>(_providerFindings.Concat(_releaseFindings));
            AddCertificateFinding(now, findings);
            return findings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<IReadOnlyList<OpsFinding>> CollectAsync(
        CollectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await request.Read(request.Now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or IOException or SocketException or DbException)
        {
            return [CreateFinding(new(CollectionRule, new() { WorkloadId = request.Component }, request.Now),
                new(OpsFindingSeverity.Warning, "A scheduled health check could not finish",
                "Check the configuration store and server's outbound connectivity. This check will retry on its next scheduled run."))];
        }
    }

    private void AddCertificateFinding(DateTimeOffset now, List<OpsFinding> findings)
    {
        if (source.CertificateExpiresAt is not { } expiry || expiry - now > TimeSpan.FromDays(60))
        {
            return;
        }
        var remaining = expiry - now;
        var window = remaining <= TimeSpan.Zero ? "expired" : remaining <= TimeSpan.FromDays(7) ? "7"
            : remaining <= TimeSpan.FromDays(30) ? "30" : "60";
        var subject = new OpsFindingSubject { WorkloadId = $"key-ring-certificate:{expiry.UtcTicks}:{window}" };
        findings.Add(CreateFinding(new(CertificateRule, subject, now), new(
            remaining <= TimeSpan.FromDays(30) ? OpsFindingSeverity.Critical : OpsFindingSeverity.Warning,
            remaining <= TimeSpan.Zero ? "The key-ring certificate has expired" : "The key-ring certificate needs renewal",
            $"The certificate protecting stored secrets expires at {expiry:O}. Ask an administrator to rotate it before expiry; keep the old key available to read existing secrets.")));
    }

    private async Task<IReadOnlyList<OpsFinding>> ReadProvidersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var providers = await source.ReadIdentityProvidersAsync(cancellationToken).ConfigureAwait(false);
        return providers.Where(provider => !provider.IsReachable).Select(provider => CreateFinding(
            new(IdentityProviderRule, new() { WorkloadId = provider.ProviderId.ToString("D") }, now),
            new(OpsFindingSeverity.Critical, "A sign-in provider cannot be reached",
            "Admin sign-in through this provider may fail. Ask an administrator to check the provider and its discovery address. Public maps continue to work."))).ToArray();
    }

    private async Task<IReadOnlyList<OpsFinding>> ReadReleaseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var release = await source.ReadLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || !OpsReleaseVersion.IsNewer(source.CurrentVersion, release.Version))
        {
            return [];
        }
        return [CreateFinding(new(ReleaseRule, new() { ReleaseVersion = release.Version }, now),
            new(OpsFindingSeverity.Info, "A new Honua version is available",
            $"Honua {release.Version} is available. Read the release notes at {release.ReleaseNotes.AbsoluteUri}. An administrator must approve an upgrade; this notice does not install it."))];
    }

    private sealed record CollectionRequest(
        Func<DateTimeOffset, CancellationToken, Task<IReadOnlyList<OpsFinding>>> Read,
        string Component, DateTimeOffset Now);

    private sealed record FindingIdentity(string Rule, OpsFindingSubject Subject, DateTimeOffset Now);
    private sealed record FindingMessage(OpsFindingSeverity Severity, string Title, string Explanation);

    private static OpsFinding CreateFinding(FindingIdentity identity, FindingMessage message) => new()
    {
        Id = OpsFindingId.Create(identity.Rule, identity.Subject), Rule = identity.Rule,
        Subject = identity.Subject, DetectedAt = identity.Now, Severity = message.Severity,
        Title = message.Title, Explanation = message.Explanation, EvidenceRefs = [identity.Rule]
    };
}
