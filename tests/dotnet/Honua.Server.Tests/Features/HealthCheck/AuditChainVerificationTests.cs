// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Infrastructure.AuditLog;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.HealthCheck;

/// <summary>
/// Covers the scheduled audit hash-chain verifier (#2810): a broken link must raise a paged signal
/// (the integrity health check flips Unhealthy) instead of being caught only on a manual /verify.
/// </summary>
public sealed class AuditChainVerificationTests
{
    [UnitTest]
    public async Task Verifier_WhenChainBroken_PublishesBrokenSignal()
    {
        var brokenReport = new AuditIntegrityReport
        {
            Verified = false,
            RowsChecked = 42,
            UnhashedRows = 0,
            FirstBrokenAuditId = 17,
            FailureReason = "entry_hash mismatch at audit_id 17",
        };
        var service = CreateService(new FakeVerifier(brokenReport));

        await service.VerifyOnceAsync(CancellationToken.None);

        service.HasVerified.Should().BeTrue();
        service.IsChainBroken.Should().BeTrue();
        service.LastReport.Should().BeSameAs(brokenReport);
        service.LastVerifiedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task Verifier_WhenChainIntact_PublishesHealthySignal()
    {
        var okReport = new AuditIntegrityReport
        {
            Verified = true,
            RowsChecked = 42,
            UnhashedRows = 3,
        };
        var service = CreateService(new FakeVerifier(okReport));

        await service.VerifyOnceAsync(CancellationToken.None);

        service.HasVerified.Should().BeTrue();
        service.IsChainBroken.Should().BeFalse();
    }

    [UnitTest]
    public async Task HealthCheck_WhenChainBroken_ReportsUnhealthy()
    {
        var brokenReport = new AuditIntegrityReport
        {
            Verified = false,
            RowsChecked = 42,
            UnhashedRows = 0,
            FirstBrokenAuditId = 17,
            FailureReason = "entry_hash mismatch at audit_id 17",
        };
        var signal = new FakeSignal
        {
            HasVerified = true,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            LastReport = brokenReport,
        };
        var sut = new AuditChainIntegrityHealthCheck(signal);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("tampered");
    }

    [UnitTest]
    public async Task HealthCheck_WhenChainIntact_ReportsHealthy()
    {
        var signal = new FakeSignal
        {
            HasVerified = true,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            LastReport = new AuditIntegrityReport { Verified = true, RowsChecked = 10, UnhashedRows = 0 },
        };
        var sut = new AuditChainIntegrityHealthCheck(signal);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public async Task HealthCheck_BeforeFirstVerification_ReportsHealthy()
    {
        var sut = new AuditChainIntegrityHealthCheck(new FakeSignal { HasVerified = false });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static AuditChainVerificationBackgroundService CreateService(IAuditLogIntegrityVerifier verifier)
    {
        var provider = new ServiceCollection()
            .AddScoped(_ => verifier)
            .BuildServiceProvider();

        return new AuditChainVerificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuditChainVerificationOptions()),
            NullLogger<AuditChainVerificationBackgroundService>.Instance);
    }

    private sealed class FakeVerifier(AuditIntegrityReport report) : IAuditLogIntegrityVerifier
    {
        public Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(report);
    }

    private sealed class FakeSignal : IAuditChainIntegritySignal
    {
        public bool HasVerified { get; init; }
        public DateTimeOffset? LastVerifiedAt { get; init; }
        public bool IsChainBroken => LastReport is { Verified: false };
        public AuditIntegrityReport? LastReport { get; init; }
    }
}
