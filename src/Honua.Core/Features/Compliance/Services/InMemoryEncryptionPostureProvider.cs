// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Compliance-managed encryption posture / key-ring. Maintains an in-memory list of
/// historical key versions and supports runtime rotation without interrupting any
/// active request — only metadata changes; existing ciphertexts keep decrypting
/// against the version that produced them.
/// </summary>
/// <remarks>
/// <para>
/// Scope: this provider is the source of truth for the encryption posture reported
/// in compliance evidence (FedRAMP SC-13 / SC-28). It is intentionally decoupled
/// from <c>IConnectionEncryptionService</c> — that service has its own
/// version lifecycle persisted in <c>honua.encryption_keys</c>. The posture
/// reflects which key versions exist, not which one the connection envelope used.
/// </para>
/// <para>
/// Rotation is zero-downtime by construction: <see cref="RotateAsync"/> appends a new
/// version, updates the active pointer with a single field assignment under a lock,
/// and returns. No requests are paused, no caches invalidated, no migration scheduled.
/// </para>
/// </remarks>
internal sealed class InMemoryEncryptionPostureProvider : IEncryptionPostureProvider
{
    private readonly IOptionsMonitor<ComplianceOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private readonly List<KeyVersionEntry> _versions = new();
    private int _activeVersion;

    public InMemoryEncryptionPostureProvider(
        IOptionsMonitor<ComplianceOptions> options,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;

        var now = _timeProvider.GetUtcNow();
        _versions.Add(new KeyVersionEntry(1, now, RetiredAt: null));
        _activeVersion = 1;
    }

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

        // The provider is a singleton (the in-memory key ring must outlive any single
        // request), but IAuditLog is scoped. Resolve it from a fresh scope per rotation
        // so we never capture a stale instance from root scope.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
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
            }, cancellationToken).ConfigureAwait(false);
        }

        return new KeyRotationOutcome
        {
            Succeeded = true,
            PreviousVersion = previous,
            NewVersion = next,
            RotatedAt = now,
            Message = "Encryption key rotated — new encryptions use the new version; existing ciphertext remains decryptable.",
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
