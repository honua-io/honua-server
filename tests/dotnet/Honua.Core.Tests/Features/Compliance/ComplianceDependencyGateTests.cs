// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance;
using Honua.Core.Features.Compliance.Domain;
using Honua.Core.Features.Compliance.Services;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="DefaultComplianceDependencyGate"/>. Pins the conservative
/// detection contract: capabilities without a real signal must require an explicit
/// operator attestation rather than report satisfied off the presence of an in-memory
/// placeholder service.
/// </summary>
public sealed class ComplianceDependencyGateTests
{
    [Fact]
    public void Sso_DefaultsToNotSatisfied_WhenOidcConfigAbsent()
    {
        var gate = CreateGate(new ComplianceOptions(), oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeFalse();
        gate.DescribeStatus(ComplianceDependency.Sso).Should().Contain("OIDC is not enabled");
    }

    [Fact]
    public void Sso_NotSatisfied_WhenOidcEnabledButNoProviderHasClientId()
    {
        var oidcConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:AzureAd:Enabled"] = "true",
            // no ClientId — provider section is half-configured
        };

        var gate = CreateGate(new ComplianceOptions(), oidcConfig);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeFalse();
    }

    [Fact]
    public void Sso_Satisfied_WhenOidcEnabledAndProviderHasClientId()
    {
        var oidcConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Google:Enabled"] = "true",
            ["Oidc:Google:ClientId"] = "google-client-id",
        };

        var gate = CreateGate(new ComplianceOptions(), oidcConfig);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeTrue();
        gate.DescribeStatus(ComplianceDependency.Sso).Should().Contain("OIDC enabled");
    }

    [Fact]
    public void Sso_OverrideForcesNotSatisfied_EvenWhenOidcLooksEnabled()
    {
        var oidcConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Google:Enabled"] = "true",
            ["Oidc:Google:ClientId"] = "google-client-id",
        };

        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { SsoConfigured = false },
        };

        var gate = CreateGate(options, oidcConfig);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeFalse();
    }

    [Fact]
    public void Rbac_DefaultsToNotSatisfied_NoAutoDetectFromInMemoryStore()
    {
        var gate = CreateGate(new ComplianceOptions(), oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.Rbac).Should().BeFalse();
        gate.DescribeStatus(ComplianceDependency.Rbac).Should().Contain("RbacConfigured");
    }

    [Fact]
    public void Rbac_OverrideTrue_ReportsSatisfied()
    {
        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { RbacConfigured = true },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.Rbac).Should().BeTrue();
    }

    [Fact]
    public void DataResidency_NotSatisfied_WhenOnlyEnforcedFlagIsSet()
    {
        // Reviewer concern: Enforced=true flips the policy view but no egress code
        // actually consults IDataResidencyPolicyProvider yet. Requires explicit attestation.
        var options = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions { Enforced = true },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.DataResidency).Should().BeFalse();
        gate.DescribeStatus(ComplianceDependency.DataResidency).Should().Contain("DataResidencyAttested");
    }

    [Fact]
    public void DataResidency_Satisfied_WhenAttested()
    {
        var options = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions { Enforced = true },
            DependencyOverrides = new ComplianceDependencyOverrides { DataResidencyAttested = true },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.DataResidency).Should().BeTrue();
    }

    [Fact]
    public void EncryptionAtRest_ProbeThrows_ReportsUnsatisfiedInsteadOfPropagating()
    {
        // ConnectionEncryptionService throws InvalidOperationException when
        // Security:ConnectionEncryption:MasterKey is missing. The compliance
        // dashboard must report the dependency as unsatisfied with a sanitized
        // message instead of letting the exception escape (which would fail
        // the entire dashboard / report request).
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionEncryptionService>(_ =>
            throw new InvalidOperationException("Master key not configured."));

        var gate = CreateGate(new ComplianceOptions(), oidcConfig: null, extraServices: services);

        gate.IsSatisfied(ComplianceDependency.EncryptionAtRest).Should().BeFalse();
        var description = gate.DescribeStatus(ComplianceDependency.EncryptionAtRest);
        description.Should().Contain("not operational");
        description.Should().Contain("InvalidOperationException");
        description.Should().NotContain("Master key not configured", "raw exception messages must not leak");
    }

    [Fact]
    public void AuditLog_ProbeThrows_ReportsUnsatisfiedInsteadOfPropagating()
    {
        // Defensive sibling of the encryption probe — if a host registers
        // IAuditLog with a factory that throws, the dashboard must keep working.
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(_ =>
            throw new InvalidOperationException("Audit sink misconfigured."));

        var gate = CreateGate(new ComplianceOptions(), oidcConfig: null, extraServices: services);

        gate.IsSatisfied(ComplianceDependency.AuditLog).Should().BeFalse();
        var description = gate.DescribeStatus(ComplianceDependency.AuditLog);
        description.Should().Contain("Audit log probe failed");
        description.Should().Contain("InvalidOperationException");
        description.Should().NotContain("misconfigured", "raw exception messages must not leak");
    }

    [Fact]
    public void AuditLog_OverrideTrue_DescribesAttestationEvenWhenProbeSaysNullSink()
    {
        // No registered IAuditLog (probe would say NullAuditLog) but operator overrides
        // to true (sidecar deployment). DescribeStatus must reflect the override source
        // rather than the disagreeing probe — otherwise the evidence row stamps an
        // Implemented status with a "falls back to NullAuditLog" detail.
        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { AuditLogConfigured = true },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.AuditLog).Should().BeTrue();
        var description = gate.DescribeStatus(ComplianceDependency.AuditLog);
        description.Should().Contain("operator override");
        description.Should().Contain("AuditLogConfigured=true");
        description.Should().NotContain("NullAuditLog", "the override source must be the authoritative description");
    }

    [Fact]
    public void AuditLog_OverrideFalse_DescribesOperatorForcedUnsatisfied()
    {
        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { AuditLogConfigured = false },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.AuditLog).Should().BeFalse();
        var description = gate.DescribeStatus(ComplianceDependency.AuditLog);
        description.Should().Contain("operator override");
        description.Should().Contain("AuditLogConfigured=false");
    }

    [Fact]
    public void Sso_OverrideTrue_DescribesAttestationEvenWhenOidcDisabled()
    {
        // OIDC is not enabled in config but operator overrides SSO to true.
        // DescribeStatus must say "operator override" instead of the misleading
        // "OIDC enabled with at least one configured provider".
        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { SsoConfigured = true },
        };

        var gate = CreateGate(options, oidcConfig: null);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeTrue();
        var description = gate.DescribeStatus(ComplianceDependency.Sso);
        description.Should().Contain("operator override");
        description.Should().Contain("SsoConfigured=true");
        description.Should().NotContain("OIDC enabled with at least one configured provider",
            "override-attested SSO must not claim live OIDC config");
    }

    [Fact]
    public void Sso_OverrideFalse_DescribesOperatorForcedUnsatisfied()
    {
        // OIDC is fully configured but operator overrides to false (auditor wants
        // to confirm gap behavior). DescribeStatus must reflect the override.
        var oidcConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Google:Enabled"] = "true",
            ["Oidc:Google:ClientId"] = "google-client-id",
        };

        var options = new ComplianceOptions
        {
            DependencyOverrides = new ComplianceDependencyOverrides { SsoConfigured = false },
        };

        var gate = CreateGate(options, oidcConfig);

        gate.IsSatisfied(ComplianceDependency.Sso).Should().BeFalse();
        var description = gate.DescribeStatus(ComplianceDependency.Sso);
        description.Should().Contain("operator override");
        description.Should().Contain("SsoConfigured=false");
    }

    private static DefaultComplianceDependencyGate CreateGate(
        ComplianceOptions options,
        IDictionary<string, string?>? oidcConfig,
        IServiceCollection? extraServices = null)
    {
        var services = extraServices ?? new ServiceCollection();
        var configBuilder = new ConfigurationBuilder();
        if (oidcConfig is not null)
        {
            configBuilder.AddInMemoryCollection(oidcConfig);
        }

        services.AddSingleton<IConfiguration>(configBuilder.Build());

        var monitor = new TestOptionsMonitor<ComplianceOptions>(options);
        return new DefaultComplianceDependencyGate(
            monitor,
            services.BuildServiceProvider(),
            NullLogger<DefaultComplianceDependencyGate>.Instance);
    }
}
