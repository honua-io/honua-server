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
