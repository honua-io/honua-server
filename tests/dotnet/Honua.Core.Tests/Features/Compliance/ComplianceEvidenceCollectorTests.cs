// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Honua.Core.Features.Compliance.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="DefaultComplianceEvidenceCollector"/> — covers the
/// "SOC 2 evidence collection can gather automated control evidence from server
/// audit and configuration state" and "dependencies are explicit and enforced"
/// acceptance criteria for #352.
/// </summary>
public sealed class ComplianceEvidenceCollectorTests
{
    [Fact]
    public async Task Snapshot_ContainsBothFrameworks()
    {
        var collector = CreateCollector();
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        snapshot.Controls.Should().NotBeEmpty();
        snapshot.Controls.Select(c => c.Control.Framework).Distinct().Should().BeEquivalentTo(
            new[] { ComplianceFramework.Soc2, ComplianceFramework.FedRamp });
    }

    [Fact]
    public async Task Snapshot_WhenNoDependenciesSatisfied_ReportsGaps()
    {
        var collector = CreateCollector();
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        var soc2cc61 = snapshot.Controls.Single(c => c.Control.ControlId == "soc2.cc6.1");
        soc2cc61.Status.Should().Be(ComplianceControlStatus.NotImplemented,
            "all dependencies for SOC 2 CC6.1 (SSO, RBAC, AuditLog) are unsatisfied in this snapshot");
        soc2cc61.Gaps.Should().NotBeEmpty();
        soc2cc61.Evidence.Should().Contain(e => e.Source == "dependency-gate");
    }

    [Fact]
    public async Task Snapshot_WhenFrameworkReadinessNotClaimed_MarksControlsNotApplicable()
    {
        var collector = CreateCollector(new ComplianceOptions
        {
            Soc2ReadinessClaimed = false,
            FedRampReadinessClaimed = true,
        });

        var snapshot = await collector.CollectAsync(CancellationToken.None);

        var soc2Controls = snapshot.Controls.Where(c => c.Control.Framework == ComplianceFramework.Soc2);
        soc2Controls.Should().AllSatisfy(c => c.Status.Should().Be(ComplianceControlStatus.NotApplicable));
    }

    [Fact]
    public async Task Snapshot_ContainsEncryptionEvidence()
    {
        var collector = CreateCollector();
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        snapshot.Encryption.ActiveKeyVersion.Should().BeGreaterThan(0);
        snapshot.Encryption.Algorithms.Should().Contain("aes-256-gcm");

        var sc28 = snapshot.Controls.Single(c => c.Control.ControlId == "fedramp.sc-28");
        sc28.Evidence.Should().Contain(e => e.Source == "encryption-posture");
    }

    [Fact]
    public async Task Snapshot_Summary_ReflectsControlStatuses()
    {
        var collector = CreateCollector();
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        var counted = snapshot.Summary.Implemented
            + snapshot.Summary.PartiallyImplemented
            + snapshot.Summary.NotImplemented
            + snapshot.Summary.NotApplicable
            + snapshot.Summary.Unknown;
        counted.Should().Be(snapshot.Controls.Count);

        snapshot.Summary.ReadinessPercent.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Snapshot_AllDependenciesUnsatisfied_DowngradesToNotImplementedEvenWithFrameworkGaps()
    {
        // fedramp.sc-8 deps = [EncryptionInTransit] (single dep). With the dep
        // unsatisfied AND FIPS not attested, AppendFrameworkSpecificEvidence appends a
        // second gap entry (FIPS). The rollup must still be NotImplemented because the
        // only dependency is unsatisfied — gaps.Count > Dependencies.Count must not
        // mask the all-failed case.
        var collector = CreateCollector(new ComplianceOptions
        {
            Soc2ReadinessClaimed = true,
            FedRampReadinessClaimed = true,
            Encryption = new ComplianceEncryptionOptions
            {
                FipsModeAttested = false,
            },
            DependencyOverrides = new ComplianceDependencyOverrides
            {
                TransportEncryptionAttested = false,
            },
        });

        var snapshot = await collector.CollectAsync(CancellationToken.None);

        var sc8 = snapshot.Controls.Single(c => c.Control.ControlId == "fedramp.sc-8");
        sc8.Control.Dependencies.Count.Should().Be(1,
            "test relies on sc-8 having exactly one overridable dependency (EncryptionInTransit)");
        sc8.Gaps.Count.Should().BeGreaterThan(sc8.Control.Dependencies.Count,
            "framework-specific evidence adds at least one extra gap beyond the dependency gaps");
        sc8.Status.Should().Be(ComplianceControlStatus.NotImplemented,
            "the only dependency is unsatisfied — the framework-specific FIPS gap must not mask the downgrade");
    }

    [Fact]
    public async Task Snapshot_AllDependenciesSatisfied_MarksControlsImplemented()
    {
        var collector = CreateCollector(new ComplianceOptions
        {
            Soc2ReadinessClaimed = true,
            FedRampReadinessClaimed = true,
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "us-gov-west-1",
            },
            Encryption = new ComplianceEncryptionOptions
            {
                FipsModeAttested = true,
            },
            DependencyOverrides = new ComplianceDependencyOverrides
            {
                AuditLogConfigured = true,
                SsoConfigured = true,
                RbacConfigured = true,
                TransportEncryptionAttested = true,
                DataResidencyAttested = true,
            },
        });

        var snapshot = await collector.CollectAsync(CancellationToken.None);

        snapshot.Controls.Where(c => c.Control.Framework == ComplianceFramework.Soc2)
            .Should().AllSatisfy(c => c.Status.Should().Be(ComplianceControlStatus.Implemented));
        snapshot.Summary.ReadinessPercent.Should().Be(100);
    }

    private static DefaultComplianceEvidenceCollector CreateCollector(ComplianceOptions? options = null)
    {
        var opts = options ?? new ComplianceOptions();
        var monitor = new TestOptionsMonitor<ComplianceOptions>(opts);
        var catalog = new DefaultComplianceControlCatalog();
        var residency = new DefaultDataResidencyPolicyProvider(monitor);
        var scopeFactory = BuildScopeFactory(NullAuditLog.Instance);
        var encryption = new InMemoryEncryptionPostureProvider(
            monitor,
            scopeFactory,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEncryptionPostureProvider>.Instance);
        var gate = new StaticDependencyGate(opts);
        return new DefaultComplianceEvidenceCollector(catalog, gate, residency, encryption, monitor, TimeProvider.System);
    }

    private static IServiceScopeFactory BuildScopeFactory(IAuditLog audit)
    {
        var services = new ServiceCollection();
        services.AddSingleton(audit);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class StaticDependencyGate : IComplianceDependencyGate
    {
        private readonly ComplianceOptions _opts;

        public StaticDependencyGate(ComplianceOptions opts) => _opts = opts;

        public bool IsSatisfied(ComplianceDependency dependency) => dependency switch
        {
            ComplianceDependency.AuditLog => _opts.DependencyOverrides.AuditLogConfigured ?? false,
            ComplianceDependency.Sso => _opts.DependencyOverrides.SsoConfigured ?? false,
            ComplianceDependency.Rbac => _opts.DependencyOverrides.RbacConfigured ?? false,
            ComplianceDependency.EncryptionAtRest => true,
            ComplianceDependency.EncryptionInTransit => _opts.DependencyOverrides.TransportEncryptionAttested ?? false,
            ComplianceDependency.DataResidency => _opts.DependencyOverrides.DataResidencyAttested ?? false,
            _ => false,
        };

        public string DescribeStatus(ComplianceDependency dependency) =>
            IsSatisfied(dependency) ? "configured" : "not-configured";
    }
}
