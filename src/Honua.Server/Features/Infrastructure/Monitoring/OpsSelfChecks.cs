// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

internal sealed record OpsSelfCheckEvaluation(IReadOnlyList<OpsFinding> Findings, IReadOnlyList<EvidenceSourceEnvelope> Sources);

internal sealed partial class OpsSelfChecks(
    TimeProvider clock, IOpsSelfCheckSource source, ILogger<OpsSelfChecks>? logger = null) : IDisposable
{
    internal const string CertificateRule = "key-ring-certificate-expiry";
    internal const string IdentityProviderRule = "idp-unreachable";
    internal const string ReleaseRule = "honua-release-available";
    internal const string CollectionRule = "self-check-unavailable";

    private readonly SemaphoreSlim _gate = new(1, 1);
    // ADR-0086 exception: these sampled health observations never authorize requests.
    // Persisted providers refresh before every five-minute probe; the configuration-independent
    // public release feed is sampled daily; a failed release read retries on a backoff that starts at
    // five minutes and doubles up to one hour. Certificate lifetime is immutable and evaluated fresh.
    private static readonly TimeSpan ReleaseInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReleaseFirstRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReleaseLongestRetry = TimeSpan.FromHours(1);
    private readonly ILogger _logger = logger ?? NullLogger<OpsSelfChecks>.Instance;
    private DateTimeOffset? _providersReadAt;
    private DateTimeOffset? _releaseDueAt;
    private TimeSpan _releaseRetry = ReleaseFirstRetry;
    private readonly OpsFindingsCollectionLedger _ledger = new();
    private OpsSelfCheckEvaluation _providerSample = new([], []);
    private OpsSelfCheckEvaluation _releaseSample = new([], []);

    public async Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default)
        => (await EvaluateWithEvidenceAsync(cancellationToken).ConfigureAwait(false)).Findings;

    public async Task<OpsSelfCheckEvaluation> EvaluateWithEvidenceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (_providersReadAt is null || now - _providersReadAt >= TimeSpan.FromMinutes(5))
            {
                (_providerSample, _) = await CollectAsync(new(ReadProvidersAsync, "sign-in-providers", TimeSpan.FromMinutes(5)), now, cancellationToken).ConfigureAwait(false);
                _providersReadAt = now;
            }
            if (_releaseDueAt is null || now >= _releaseDueAt)
            {
                (_releaseSample, var succeeded) = await CollectAsync(new(ReadReleaseAsync, "public-release-feed", ReleaseInterval), now, cancellationToken).ConfigureAwait(false);
                _releaseDueAt = now + (succeeded ? ReleaseInterval : _releaseRetry);
                _releaseRetry = succeeded ? ReleaseFirstRetry : LongerRetry(_releaseRetry);
            }
            var findings = new List<OpsFinding>(_providerSample.Findings.Concat(_releaseSample.Findings));
            AddCertificateFinding(now, findings);
            return new(findings, [.. _providerSample.Sources, .. _releaseSample.Sources]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static TimeSpan LongerRetry(TimeSpan retry)
        => TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, ReleaseLongestRetry.Ticks));

    private async Task<(OpsSelfCheckEvaluation Sample, bool Succeeded)> CollectAsync(
        CollectionRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var collection = new OpsFindingsStoreCollection("honua_ops_findings.self-check." + request.Component,
            EvidencePostureVocabulary.BackendKinds.InProcess, request.Component, clock);
        var result = await collection.ReadAsync(request.Component,
            token => ReadAndLogFailureAsync(request, now, token), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OpsFinding> findings = result.Succeeded ? result.Value!
            : [CreateFinding(new(CollectionRule, new() { WorkloadId = request.Component }, now),
                new(OpsFindingSeverity.Warning, "A scheduled health check could not finish",
                "Check the configuration store and server's outbound connectivity. This check will retry on its next scheduled run."))];
        return (new(findings, [collection.Publish(_ledger, request.MaximumAge)]), result.Succeeded);
    }

    private async Task<IReadOnlyList<OpsFinding>> ReadAndLogFailureAsync(
        CollectionRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await request.Read(now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception text can carry connection details, so only the failure class is logged.
            CollectionFailed(_logger, request.Component, DescribeFailure(ex));
            throw;
        }
    }

    internal static string DescribeFailure(Exception exception)
    {
        var description = exception switch
        {
            HttpRequestException { StatusCode: { } status } => $"{exception.GetType().Name} status {(int)status}",
            HttpRequestException http => $"{exception.GetType().Name} {http.HttpRequestError}",
            _ => exception.GetType().Name
        };
        var cause = exception.GetBaseException();
        return cause switch
        {
            _ when ReferenceEquals(cause, exception) => description,
            SocketException socket => $"{description} ({nameof(SocketException)} {socket.SocketErrorCode})",
            _ => $"{description} ({cause.GetType().Name})"
        };
    }

    [LoggerMessage(EventId = 7463, Level = LogLevel.Warning,
        Message = "Scheduled self-check {Component} could not finish: {FailureClass}.")]
    private static partial void CollectionFailed(ILogger logger, string component, string failureClass);

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
        string Component, TimeSpan MaximumAge);

    private sealed record FindingIdentity(string Rule, OpsFindingSubject Subject, DateTimeOffset Now);
    private sealed record FindingMessage(OpsFindingSeverity Severity, string Title, string Explanation);

    private static OpsFinding CreateFinding(FindingIdentity identity, FindingMessage message) => new()
    {
        Id = OpsFindingId.Create(identity.Rule, identity.Subject),
        Rule = identity.Rule,
        Subject = identity.Subject,
        DetectedAt = identity.Now,
        Severity = message.Severity,
        Title = message.Title,
        Explanation = message.Explanation,
        EvidenceRefs = [identity.Rule]
    };
}
