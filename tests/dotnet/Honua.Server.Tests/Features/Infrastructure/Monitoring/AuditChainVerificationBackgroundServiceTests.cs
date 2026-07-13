// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class AuditChainVerificationBackgroundServiceTests
{
    [UnitTest]
    public async Task VerifyOnce_PublishesReportToSignal()
    {
        var report = new AuditIntegrityReport
        {
            Verified = false,
            RowsChecked = 10,
            UnhashedRows = 0,
            FirstBrokenAuditId = 3,
            FailureReason = "hash diverged",
        };
        var signal = new AuditChainVerificationSignal();
        var sut = Create(signal, verifier: new FakeVerifier(report));

        await sut.VerifyOnceAsync();

        signal.LastReport.Should().BeSameAs(report);
        signal.LastVerifiedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task VerifyOnce_WhenNoVerifierRegistered_IsNoOp()
    {
        var signal = new AuditChainVerificationSignal();
        var sut = Create(signal, verifier: null);

        await sut.VerifyOnceAsync();

        signal.LastReport.Should().BeNull();
        signal.LastVerifiedAt.Should().BeNull();
    }

    [UnitTest]
    public async Task VerifyOnce_WhenVerifierThrows_LeavesPreviousResultAndDoesNotThrow()
    {
        var signal = new AuditChainVerificationSignal();
        signal.Publish(new AuditIntegrityReport { Verified = true, RowsChecked = 1, UnhashedRows = 0 }, DateTimeOffset.UtcNow);
        var previous = signal.LastReport;
        var sut = Create(signal, verifier: new ThrowingVerifier());

        var act = async () => await sut.VerifyOnceAsync();

        await act.Should().NotThrowAsync();
        signal.LastReport.Should().BeSameAs(previous);
    }

    private static AuditChainVerificationBackgroundService Create(
        AuditChainVerificationSignal signal,
        IAuditLogIntegrityVerifier? verifier)
    {
        var services = new ServiceCollection();
        if (verifier is not null)
        {
            services.AddScoped(_ => verifier);
        }

        var provider = services.BuildServiceProvider();
        return new AuditChainVerificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            new StaticOptionsMonitor<AuditChainVerificationOptions>(new AuditChainVerificationOptions()),
            TimeProvider.System,
            NullLogger<AuditChainVerificationBackgroundService>.Instance);
    }

    private sealed class FakeVerifier(AuditIntegrityReport report) : IAuditLogIntegrityVerifier
    {
        public Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(report);
    }

    private sealed class ThrowingVerifier : IAuditLogIntegrityVerifier
    {
        public Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
