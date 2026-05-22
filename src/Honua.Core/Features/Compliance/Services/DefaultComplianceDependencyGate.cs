// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
internal sealed partial class DefaultComplianceDependencyGate : IComplianceDependencyGate
{
    // Known OIDC provider section names under "Oidc". Mirrors OidcAuthenticationOptions
    // (Honua.Server) — kept here as a literal list to avoid a Honua.Core → Honua.Server
    // dependency just to read configuration.
    private static readonly string[] OidcProviderSections = ["AzureAd", "Google", "Generic", "Okta", "Auth0"];

    private readonly IOptionsMonitor<ComplianceOptions> _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<DefaultComplianceDependencyGate> _logger;

    public DefaultComplianceDependencyGate(
        IOptionsMonitor<ComplianceOptions> options,
        IServiceProvider services,
        ILogger<DefaultComplianceDependencyGate> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _services = services;
        _logger = logger;
    }

    [LoggerMessage(EventId = 4730, Level = LogLevel.Warning,
        Message = "Compliance dependency probe for {Dependency} failed; reporting dependency as unsatisfied.")]
    private partial void LogProbeFailed(string dependency, Exception exception);

    public bool IsSatisfied(ComplianceDependency dependency)
    {
        var overrides = _options.CurrentValue.DependencyOverrides;
        return dependency switch
        {
            ComplianceDependency.AuditLog => overrides.AuditLogConfigured ?? IsAuditLogOperational(out _),
            ComplianceDependency.Sso => overrides.SsoConfigured ?? IsOidcEffectivelyEnabled(),
            ComplianceDependency.Rbac => overrides.RbacConfigured ?? false,
            ComplianceDependency.EncryptionAtRest => IsEncryptionAtRestOperational(out _),
            ComplianceDependency.EncryptionInTransit => overrides.TransportEncryptionAttested ?? false,
            ComplianceDependency.DataResidency => overrides.DataResidencyAttested ?? false,
            _ => false,
        };
    }

    public string DescribeStatus(ComplianceDependency dependency)
    {
        return dependency switch
        {
            ComplianceDependency.AuditLog => IsAuditLogOperational(out var auditDetail)
                ? "Durable audit log sink registered (PostgresAuditLog)."
                : auditDetail,
            ComplianceDependency.Sso => IsSatisfied(dependency)
                ? "OIDC enabled with at least one configured provider."
                : "OIDC is not enabled or no provider has a client ID — set Oidc:Enabled=true with at least one provider, or attest via Compliance:DependencyOverrides:SsoConfigured.",
            ComplianceDependency.Rbac => IsSatisfied(dependency)
                ? "RBAC enforcement attested by operator."
                : "RBAC enforcement is not attested — set Compliance:DependencyOverrides:RbacConfigured once role policies are required on protected endpoints.",
            ComplianceDependency.EncryptionAtRest => IsEncryptionAtRestOperational(out var encryptionDetail)
                ? "Encryption-at-rest service registered (AES-256-GCM envelope)."
                : encryptionDetail,
            ComplianceDependency.EncryptionInTransit => IsSatisfied(dependency)
                ? "Operator attests TLS / HTTPS is enforced upstream of the application."
                : "Transport encryption not attested — set Compliance:DependencyOverrides:TransportEncryptionAttested.",
            ComplianceDependency.DataResidency => IsSatisfied(dependency)
                ? "Operator attests data residency is enforced at egress boundaries."
                : "Data residency enforcement is not attested — Compliance:DataResidency:Enforced controls the policy view, but operators must also set Compliance:DependencyOverrides:DataResidencyAttested once egress guards are wired.",
            _ => "Unknown dependency.",
        };
    }

    // Probe methods are intentionally exception-tolerant: a singleton factory that
    // throws on construction (e.g. ConnectionEncryptionService when MasterKey is
    // unset) must NOT crash the compliance dashboard. The dashboard's job is to
    // report the gap, not propagate it. We log the failure and report the
    // dependency as unsatisfied with a sanitized operator-facing message.
    private bool IsAuditLogOperational(out string failureReason)
    {
        try
        {
            var auditLog = _services.GetService(typeof(IAuditLog));
            if (auditLog is null)
            {
                failureReason = "Audit log sink falls back to NullAuditLog — events are not persisted.";
                return false;
            }

            var typeName = auditLog.GetType().FullName ?? string.Empty;
            if (typeName.Contains("NullAuditLog", StringComparison.Ordinal))
            {
                failureReason = "Audit log sink falls back to NullAuditLog — events are not persisted.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogProbeFailed(nameof(ComplianceDependency.AuditLog), ex);
            failureReason = $"Audit log probe failed ({ex.GetType().Name}) — reporting dependency as unsatisfied. See logs.";
            return false;
        }
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

    private bool IsEncryptionAtRestOperational(out string failureReason)
    {
        try
        {
            var encryption = _services.GetService(typeof(Security.Abstractions.IConnectionEncryptionService));
            if (encryption is null)
            {
                failureReason = "Encryption-at-rest service not registered for this deployment.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // ConnectionEncryptionService.DeriveMasterKey throws InvalidOperationException
            // when Security:ConnectionEncryption:MasterKey is missing or too short. The
            // dashboard must keep functioning when this happens.
            LogProbeFailed(nameof(ComplianceDependency.EncryptionAtRest), ex);
            failureReason = $"Encryption-at-rest service is not operational (probe threw {ex.GetType().Name}). Check Security:ConnectionEncryption:* configuration.";
            return false;
        }
    }
}
