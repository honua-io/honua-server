// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsSelfChecksTests
{
    [UnitTheory]
    [InlineData(60, OpsFindingSeverity.Warning)]
    [InlineData(31, OpsFindingSeverity.Warning)]
    [InlineData(30, OpsFindingSeverity.Critical)]
    [InlineData(10, OpsFindingSeverity.Critical)]
    [InlineData(7, OpsFindingSeverity.Critical)]
    [InlineData(0, OpsFindingSeverity.Critical)]
    [InlineData(-1, OpsFindingSeverity.Critical)]
    [Operation(Operations.TestInfrastructure)]
    public async Task Certificate_ExpiryWindows_ProduceHumanActionFinding(int days, OpsFindingSeverity severity)
    {
        var clock = new TestClock();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=self-check-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(clock.Now.AddDays(-90), clock.Now.AddDays(days));
        var source = new FakeSource { CertificateExpiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()) };

        var finding = Assert.Single(await new OpsSelfChecks(clock, source).EvaluateAsync());

        Assert.Equal(OpsSelfChecks.CertificateRule, finding.Rule);
        Assert.Equal(severity, finding.Severity);
        Assert.Null(finding.RecommendedAction);
        Assert.Contains("certificate", finding.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rotate", finding.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Certificate_OutsideWindowOrNotConfigured_ProducesNoFinding()
    {
        var clock = new TestClock();
        var source = new FakeSource { CertificateExpiresAt = clock.Now.AddDays(61) };
        Assert.Empty(await new OpsSelfChecks(clock, source).EvaluateAsync());
        source.CertificateExpiresAt = null;
        Assert.Empty(await new OpsSelfChecks(clock, source).EvaluateAsync());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Certificate_CrossesThreshold_ChangesFindingIdentityForNotification()
    {
        var clock = new TestClock();
        var source = new FakeSource { CertificateExpiresAt = clock.Now.AddDays(31) };
        var checks = new OpsSelfChecks(clock, source);
        var warning = Assert.Single(await checks.EvaluateAsync());
        clock.Advance(TimeSpan.FromDays(1));
        var critical = Assert.Single(await checks.EvaluateAsync());
        Assert.NotEqual(warning.Id, critical.Id);
        clock.Advance(TimeSpan.FromDays(23));
        var urgent = Assert.Single(await checks.EvaluateAsync());
        Assert.NotEqual(critical.Id, urgent.Id);
        clock.Advance(TimeSpan.FromDays(8));
        Assert.NotEqual(urgent.Id, Assert.Single(await checks.EvaluateAsync()).Id);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task IdentityProvider_UnreachableCachedForFiveMinutes_ThenRecoveryClearsFinding()
    {
        var clock = new TestClock();
        var providerId = Guid.NewGuid();
        var source = new FakeSource { Providers = [new(providerId, false), new(Guid.NewGuid(), true)] };
        var checks = new OpsSelfChecks(clock, source);
        var finding = Assert.Single(await checks.EvaluateAsync());
        Assert.Equal(OpsSelfChecks.IdentityProviderRule, finding.Rule);
        Assert.Equal(OpsFindingSeverity.Critical, finding.Severity);
        Assert.Equal(providerId.ToString("D"), finding.Subject.WorkloadId);
        Assert.Null(finding.RecommendedAction);

        source.Providers = [new(providerId, true)];
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(finding.Id, Assert.Single(await checks.EvaluateAsync()).Id);
        Assert.Equal(1, source.ProviderReads);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await checks.EvaluateAsync());
        Assert.Equal(2, source.ProviderReads);
        Assert.Equal(1, source.ReleaseReads);
    }

    [UnitTheory]
    [InlineData("2026.1-rc.2", "2026.1-rc.3", true)]
    [InlineData("2026.1-rc.9", "2026.1-rc.10", true)]
    [InlineData("2026.1-rc.3", "2026.1", true)]
    [InlineData("2026.1", "2026.1-rc.3", false)]
    [InlineData("2026.1", "2026.2", true)]
    [InlineData("2026.2", "2026.1", false)]
    [InlineData("2026.1+commit", "v2026.1", false)]
    [InlineData("unknown", "2026.1", false)]
    [InlineData("2026.1", "garbage", false)]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_OnlyNewerVersion_ProducesInformation(string current, string latest, bool newer)
    {
        var source = new FakeSource { CurrentVersion = current, Release = new(latest, ReleaseNotes) };
        var findings = await new OpsSelfChecks(new TestClock(), source).EvaluateAsync();
        if (!newer)
        {
            Assert.DoesNotContain(findings, finding => finding.Rule == OpsSelfChecks.ReleaseRule);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal(OpsSelfChecks.ReleaseRule, finding.Rule);
        Assert.Equal(OpsFindingSeverity.Info, finding.Severity);
        Assert.Contains(ReleaseNotes.AbsoluteUri, finding.Explanation, StringComparison.Ordinal);
        Assert.Null(finding.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_DailyCadence_UsesCachedObservationUntilNextDay()
    {
        var clock = new TestClock();
        var source = new FakeSource { Release = new("2026.2", ReleaseNotes) };
        var checks = new OpsSelfChecks(clock, source);
        var first = Assert.Single(await checks.EvaluateAsync());
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(first.Id, Assert.Single(await checks.EvaluateAsync()).Id);
        Assert.Equal(1, source.ReleaseReads);
        source.Release = null;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await checks.EvaluateAsync());
        Assert.Equal(2, source.ReleaseReads);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConcurrentEvaluations_ShareOneProbePerCadence()
    {
        var source = new FakeSource { ProbeGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var checks = new OpsSelfChecks(new TestClock(), source);
        var reads = Enumerable.Range(0, 12).Select(_ => checks.EvaluateAsync()).ToArray();
        source.ProbeGate.SetResult();
        await Task.WhenAll(reads);
        Assert.Equal(1, source.ProviderReads);
        Assert.Equal(1, source.ReleaseReads);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task FailedCollection_ReportsUnavailableWithoutLeakingException_AndOtherChecksContinue()
    {
        var source = new FakeSource
        {
            ProviderFailure = new HttpRequestException("secret-client-key private-authority.internal"),
            Release = new("2026.2", ReleaseNotes)
        };
        var findings = await new OpsSelfChecks(new TestClock(), source).EvaluateAsync();
        Assert.Contains(findings, finding => finding.Rule == OpsSelfChecks.CollectionRule && finding.Severity == OpsFindingSeverity.Warning);
        Assert.Contains(findings, finding => finding.Rule == OpsSelfChecks.ReleaseRule);
        Assert.DoesNotContain("secret-client-key", JsonSerializer.Serialize(findings), StringComparison.Ordinal);
        Assert.DoesNotContain("private-authority", JsonSerializer.Serialize(findings), StringComparison.Ordinal);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConfigurationStoreFailure_ReportsWarningAndRetriesWithoutStoppingOtherChecks()
    {
        var clock = new TestClock();
        var source = new FakeSource
        {
            ProviderFailure = new Npgsql.NpgsqlException("private-store-connection"),
            Release = new("2026.2", ReleaseNotes)
        };
        var checks = new OpsSelfChecks(clock, source);
        var findings = await checks.EvaluateAsync();
        Assert.Contains(findings, finding => finding.Rule == OpsSelfChecks.CollectionRule);
        Assert.Contains(findings, finding => finding.Rule == OpsSelfChecks.ReleaseRule);
        Assert.DoesNotContain("private-store-connection", JsonSerializer.Serialize(findings), StringComparison.Ordinal);
        source.ProviderFailure = null;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(await checks.EvaluateAsync(), finding => finding.Rule == OpsSelfChecks.CollectionRule);
        Assert.Equal(2, source.ProviderReads);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CancelledCollection_PropagatesAndDoesNotCacheIncompleteResult()
    {
        var source = new FakeSource { ProviderFailure = new OperationCanceledException() };
        var checks = new OpsSelfChecks(new TestClock(), source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checks.EvaluateAsync());
        source.ProviderFailure = null;
        await checks.EvaluateAsync();
        Assert.Equal(2, source.ProviderReads);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task FailedCollection_EvidenceStaysUnavailableUntilSuccessfulRetry()
    {
        var clock = new TestClock();
        var source = new FakeSource { Providers = [new(Guid.NewGuid(), false)] };
        using var checks = new OpsSelfChecks(clock, source);
        var original = await checks.EvaluateWithEvidenceAsync();
        var observed = original.Sources[0].ObservedAt;
        Assert.Equal(OpsSelfChecks.IdentityProviderRule, Assert.Single(original.Findings).Rule);

        source.ProviderFailure = new InvalidOperationException();
        clock.Advance(TimeSpan.FromMinutes(5));
        var failed = await checks.EvaluateWithEvidenceAsync();
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, failed.Sources[0].Completeness);
        Assert.False(EvidencePostureFactory.IsActionable(failed.Sources[0]));
        Assert.Equal(observed, failed.Sources[0].ObservedAt);
        Assert.Equal(observed, failed.Sources[0].LastSuccessfulAt);
        Assert.Equal(OpsSelfChecks.CollectionRule, Assert.Single(failed.Findings).Rule);

        source.ProviderFailure = null;
        source.Providers = [];
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(failed.Sources, (await checks.EvaluateWithEvidenceAsync()).Sources);
        Assert.Equal(2, source.ProviderReads);
        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await checks.EvaluateWithEvidenceAsync();
        Assert.Empty(recovered.Findings);
        Assert.True(EvidencePostureFactory.IsActionable(recovered.Sources[0]));
        Assert.Equal(clock.Now, recovered.Sources[0].ObservedAt);
    }

    private static readonly Uri ReleaseNotes = new("https://github.com/honua-io/honua-server/releases/tag/v2026.2");

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan interval) => Now += interval;
    }

    private sealed class FakeSource : IOpsSelfCheckSource
    {
        public DateTimeOffset? CertificateExpiresAt { get; set; }
        public string CurrentVersion { get; init; } = "2026.1";
        public IReadOnlyList<OpsIdentityProviderHealth> Providers { get; set; } = [];
        public OpsPublishedRelease? Release { get; set; }
        public Exception? ProviderFailure { get; set; }
        public TaskCompletionSource? ProbeGate { get; init; }
        public int ProviderReads { get; private set; }
        public int ReleaseReads { get; private set; }

        public async Task<IReadOnlyList<OpsIdentityProviderHealth>> ReadIdentityProvidersAsync(CancellationToken cancellationToken)
        {
            ProviderReads++;
            if (ProbeGate is not null)
            {
                await ProbeGate.Task.WaitAsync(cancellationToken);
            }
            if (ProviderFailure is not null)
            {
                throw ProviderFailure;
            }
            return Providers;
        }

        public Task<OpsPublishedRelease?> ReadLatestReleaseAsync(CancellationToken cancellationToken)
        {
            ReleaseReads++;
            return Task.FromResult(Release);
        }
    }
}
