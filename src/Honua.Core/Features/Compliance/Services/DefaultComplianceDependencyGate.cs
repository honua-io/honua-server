// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Inspects the live DI container to decide whether each compliance dependency is
/// operational. Detection is intentionally conservative — when in doubt, the gate
/// reports "not satisfied" so the snapshot under-claims rather than over-claims.
/// </summary>
/// <remarks>
/// Several capabilities do not yet have first-class capability probes (RBAC roles
/// actually enforced on endpoints, residency egress guards wired into boundary
/// call-sites). For those we require an explicit operator attestation through
/// <see cref="ComplianceDependencyOverrides"/> rather than rely on the presence of
/// an in-memory placeholder service. This keeps the dashboard honest until a real
/// signal exists; see <c>docs/operator/compliance-framework.md</c>.
/// </remarks>
internal sealed class DefaultComplianceDependencyGate : IComplianceDependencyGate
{
    // Known OIDC provider section names under "Oidc". Mirrors OidcAuthenticationOptions
    // (Honua.Server) — kept here as a literal list to avoid a Honua.Core → Honua.Server
    // dependency just to read configuration.
    private static readonly string[] OidcProviderSections = ["AzureAd", "Google", "Generic", "Okta", "Auth0"];

    private readonly IOptionsMonitor<ComplianceOptions> _options;
    private readonly IServiceProvider _services;

    public DefaultComplianceDependencyGate(IOptionsMonitor<ComplianceOptions> options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        _options = options;
        _services = services;
    }

    public bool IsSatisfied(ComplianceDependency dependency)
    {
        var overrides = _options.CurrentValue.DependencyOverrides;
        return dependency switch
        {
            ComplianceDependency.AuditLog => overrides.AuditLogConfigured ?? IsAuditLogOperational(),
            ComplianceDependency.Sso => overrides.SsoConfigured ?? IsOidcEffectivelyEnabled(),
            ComplianceDependency.Rbac => overrides.RbacConfigured ?? false,
            ComplianceDependency.EncryptionAtRest => IsEncryptionAtRestOperational(),
            ComplianceDependency.EncryptionInTransit => overrides.TransportEncryptionAttested ?? false,
            ComplianceDependency.DataResidency => overrides.DataResidencyAttested ?? false,
            _ => false,
        };
    }

    public string DescribeStatus(ComplianceDependency dependency)
    {
        return dependency switch
        {
            ComplianceDependency.AuditLog => IsSatisfied(dependency)
                ? "Durable audit log sink registered (PostgresAuditLog)."
                : "Audit log sink falls back to NullAuditLog — events are not persisted.",
            ComplianceDependency.Sso => IsSatisfied(dependency)
                ? "OIDC enabled with at least one configured provider."
                : "OIDC is not enabled or no provider has a client ID — set Oidc:Enabled=true with at least one provider, or attest via Compliance:DependencyOverrides:SsoConfigured.",
            ComplianceDependency.Rbac => IsSatisfied(dependency)
                ? "RBAC enforcement attested by operator."
                : "RBAC enforcement is not attested — set Compliance:DependencyOverrides:RbacConfigured once role policies are required on protected endpoints.",
            ComplianceDependency.EncryptionAtRest => IsSatisfied(dependency)
                ? "Encryption-at-rest service registered (AES-256-GCM envelope)."
                : "Encryption-at-rest service not registered for this deployment.",
            ComplianceDependency.EncryptionInTransit => IsSatisfied(dependency)
                ? "Operator attests TLS / HTTPS is enforced upstream of the application."
                : "Transport encryption not attested — set Compliance:DependencyOverrides:TransportEncryptionAttested.",
            ComplianceDependency.DataResidency => IsSatisfied(dependency)
                ? "Operator attests data residency is enforced at egress boundaries."
                : "Data residency enforcement is not attested — Compliance:DataResidency:Enforced controls the policy view, but operators must also set Compliance:DependencyOverrides:DataResidencyAttested once egress guards are wired.",
            _ => "Unknown dependency.",
        };
    }

    private bool IsAuditLogOperational()
    {
        var auditLog = _services.GetService(typeof(IAuditLog));
        if (auditLog is null)
        {
            return false;
        }

        var typeName = auditLog.GetType().FullName ?? string.Empty;
        return !typeName.Contains("NullAuditLog", StringComparison.Ordinal);
    }

    private bool IsOidcEffectivelyEnabled()
    {
        // Authoritative signal: configuration. IOidcProviderStore is registered
        // unconditionally as an in-memory placeholder, so its presence is not a
        // capability proof — see honua-server-admin tracker for the durable store.
        var configuration = _services.GetService<IConfiguration>();
        if (configuration is null)
        {
            return false;
        }

        var oidc = configuration.GetSection("Oidc");
        if (!oidc.Exists() || !oidc.GetValue("Enabled", false))
        {
            return false;
        }

        for (var i = 0; i < OidcProviderSections.Length; i++)
        {
            var provider = oidc.GetSection(OidcProviderSections[i]);
            if (!provider.Exists())
            {
                continue;
            }

            if (provider.GetValue("Enabled", false)
                && !string.IsNullOrWhiteSpace(provider["ClientId"]))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsEncryptionAtRestOperational()
    {
        var encryption = _services.GetService(typeof(Security.Abstractions.IConnectionEncryptionService));
        return encryption is not null;
    }
}
