// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Compliance-managed encryption posture / key-version timeline. Maintains an
/// in-memory list of auditor-facing key-version entries and advances the posture
/// counter on <see cref="RotateAsync"/>. The provider stores version metadata
/// only — no cipher key material, no ciphertext re-encryption, no envelope
/// rewrap. Real key-material rotation is the responsibility of the
/// <c>Honua.Postgres.Features.Security.IConnectionEncryptionService</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Scope: this provider is the source of truth for the encryption posture reported
/// in compliance evidence (FedRAMP SC-13 / SC-28). It is intentionally decoupled
/// from <c>IConnectionEncryptionService</c> — that service has its own version
/// lifecycle persisted in <c>honua.encryption_keys</c>. The posture reflects which
/// version events the operator has recorded for auditor traceability, not which
/// key the connection envelope used.
/// </para>
/// <para>
/// <see cref="RotateAsync"/> appends a new version, updates the active pointer with a
/// single field assignment under a lock, and returns. The operation does not pause
/// requests, invalidate caches, or schedule data migration — those concerns live in
/// the connection-encryption code path. Operators wanting to rotate cipher material
/// must also follow the runbook for <c>Security:ConnectionEncryption:MasterKey</c>.
/// </para>
/// </remarks>
internal sealed partial class InMemoryEncryptionPostureProvider : IEncryptionPostureProvider
{
    // Bounded timeout for the post-commit audit write. Kept short — the rotation has
    // already mutated in-memory state, and we never want a slow audit sink to look
    // like a hung rotation. Long enough to absorb a typical Postgres insert latency.
    private static readonly TimeSpan AuditTimeout = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<ComplianceOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryEncryptionPostureProvider> _logger;

    private readonly object _gate = new();
    private readonly List<KeyVersionEntry> _versions = new();
    private int _activeVersion;

    public InMemoryEncryptionPostureProvider(
        IOptionsMonitor<ComplianceOptions> options,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<InMemoryEncryptionPostureProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        var now = _timeProvider.GetUtcNow();
        _versions.Add(new KeyVersionEntry(1, now, RetiredAt: null));
        _activeVersion = 1;
    }

    [LoggerMessage(EventId = 4720, Level = LogLevel.Error,
        Message = "Compliance key rotation v{Previous}->v{Next} committed but audit-log write failed; rotation state advanced without a persisted audit event.")]
    private partial void LogAuditWriteFailed(int previous, int next, Exception exception);

    public EncryptionPosture GetPosture()
    {
        var opts = _options.CurrentValue;

        lock (_gate)
        {
            var activeEntry = FindEntry(_activeVersion);
            var lastRotation = _versions.Count > 1 ? activeEntry.IssuedAt : (DateTimeOffset?)null;

            return new EncryptionPosture
            {
                FipsMode = ResolveFipsMode(opts),
                FipsSource = ResolveFipsSource(opts),
                Algorithms = opts.Encryption.Algorithms.AsReadOnly(),
                ActiveKeyVersion = _activeVersion,
                RetainedKeyVersions = _versions.Count,
                ActiveKeyIssuedAt = activeEntry.IssuedAt,
                LastRotationAt = lastRotation,
            };
        }
    }

    public async Task<KeyRotationOutcome> RotateAsync(string requestedBy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        int previous;
        int next;

        lock (_gate)
        {
            previous = _activeVersion;
            next = previous + 1;

            // Retire the previous version (still usable for decryption) and append the new active one.
            var index = _versions.FindIndex(v => v.Version == previous);
            if (index >= 0)
            {
                _versions[index] = _versions[index] with { RetiredAt = now };
            }

            _versions.Add(new KeyVersionEntry(next, now, RetiredAt: null));
            _activeVersion = next;
        }

        // Rotation state is now committed. The audit write must NOT cancel just because
        // the caller's request token did — that would leave the key version advanced
        // with no persisted audit trail (SOC 2 CC6 / FedRAMP IR-4 violation). Use a
        // bounded, request-independent token and treat failures as best-effort.
        using var auditCts = new CancellationTokenSource(AuditTimeout);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            await auditLog.RecordAsync(new AuditEvent
            {
                Timestamp = now,
                EventType = AuditEventType.ConfigChange,
                Actor = string.IsNullOrWhiteSpace(requestedBy) ? AuditEvent.AnonymousActor : requestedBy,
                ActorType = AuditActorType.UserId,
                ResourceType = "encryption-key",
                ResourceId = next.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = "encryption.key.rotate",
                Outcome = AuditOutcome.Success,
                CorrelationId = $"rotate-{next}",
                Details = $"{{\"previousVersion\":{previous},\"newVersion\":{next}}}",
            }, auditCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Don't roll back the in-memory rotation — it's already visible to GetPosture.
            // Surface the failure as a high-signal log line so operators can reconcile.
            LogAuditWriteFailed(previous, next, ex);
        }

        return new KeyRotationOutcome
        {
            Succeeded = true,
            PreviousVersion = previous,
            NewVersion = next,
            RotatedAt = now,
            Message = $"Compliance key-version posture advanced from v{previous} to v{next}. This records an auditor-facing rotation event; actual cipher key material is managed by IConnectionEncryptionService and is unaffected by this endpoint.",
        };
    }

    private static bool ResolveFipsMode(ComplianceOptions opts)
    {
        if (opts.Encryption.FipsModeAttested)
        {
            return true;
        }

        // .NET 8+: AesGcm / AesCcm wrap OpenSSL/CNG, both of which honour the OS FIPS
        // mode. Direct probing through .NET requires reflection; the operator attests
        // FIPS posture via configuration and the collector cross-checks runtime hints.
        var fipsEnabledHint = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_SECURITY_CRYPTOGRAPHY_USEFIPS");
        return string.Equals(fipsEnabledHint, "1", StringComparison.Ordinal)
            || string.Equals(fipsEnabledHint, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFipsSource(ComplianceOptions opts)
    {
        if (opts.Encryption.FipsModeAttested)
        {
            return "operator-attested";
        }

        var fipsEnabledHint = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_SECURITY_CRYPTOGRAPHY_USEFIPS");
        if (string.Equals(fipsEnabledHint, "1", StringComparison.Ordinal)
            || string.Equals(fipsEnabledHint, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "runtime-environment-variable";
        }

        return "unverified";
    }

    private KeyVersionEntry FindEntry(int version)
    {
        for (var i = 0; i < _versions.Count; i++)
        {
            if (_versions[i].Version == version)
            {
                return _versions[i];
            }
        }

        return new KeyVersionEntry(version, _timeProvider.GetUtcNow(), RetiredAt: null);
    }

    private readonly record struct KeyVersionEntry(int Version, DateTimeOffset IssuedAt, DateTimeOffset? RetiredAt);
}
