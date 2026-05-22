// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Inspects the live DI container to decide whether each compliance dependency is
/// operational. Detection is intentionally conservative — when in doubt, the gate
/// reports "not satisfied" so the snapshot under-claims rather than over-claims.
/// </summary>
internal sealed class DefaultComplianceDependencyGate : IComplianceDependencyGate
{
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
            ComplianceDependency.Sso => overrides.SsoConfigured ?? IsSsoConfigured(),
            ComplianceDependency.Rbac => overrides.RbacConfigured ?? IsRbacConfigured(),
            ComplianceDependency.EncryptionAtRest => IsEncryptionAtRestOperational(),
            ComplianceDependency.EncryptionInTransit => overrides.TransportEncryptionAttested ?? false,
            ComplianceDependency.DataResidency => IsResidencyEnforced(),
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
                ? "OIDC identity provider registered."
                : "OIDC identity provider not configured — only API-key authentication is available.",
            ComplianceDependency.Rbac => IsSatisfied(dependency)
                ? "Role store registered — RBAC primitives are available."
                : "Role store not registered — RBAC is not enforced.",
            ComplianceDependency.EncryptionAtRest => IsSatisfied(dependency)
                ? "Encryption-at-rest service registered (AES-256-GCM envelope)."
                : "Encryption-at-rest service not registered for this deployment.",
            ComplianceDependency.EncryptionInTransit => IsSatisfied(dependency)
                ? "Operator attests TLS / HTTPS is enforced upstream of the application."
                : "Transport encryption not attested — set Compliance:DependencyOverrides:TransportEncryptionAttested.",
            ComplianceDependency.DataResidency => IsSatisfied(dependency)
                ? "Data-residency policy is enforced."
                : "Data-residency policy is informational only (not enforced).",
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

    private bool IsSsoConfigured()
    {
        var providerStore = _services.GetService(typeof(Identity.Abstractions.IOidcProviderStore));
        return providerStore is not null;
    }

    private bool IsRbacConfigured()
    {
        var roleStore = _services.GetService(typeof(Authorization.Abstractions.IRoleStore));
        return roleStore is not null;
    }

    private bool IsEncryptionAtRestOperational()
    {
        var encryption = _services.GetService(typeof(Security.Abstractions.IConnectionEncryptionService));
        return encryption is not null;
    }

    private bool IsResidencyEnforced() => _options.CurrentValue.DataResidency.Enforced;
}
