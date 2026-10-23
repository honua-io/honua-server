// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Supported unlicensed runtime for 2026.1. Owns no files, secrets, timers or background work.
/// Runtime feature opt-ins and authorization remain the responsibility of their existing gates.
/// </summary>
internal sealed class DisabledLicenseService : ILicenseEntitlementService, ILicenseStatusProvider,
    ILicenseManager, ILicenseOperationPolicy
{
    internal const string UploadMessage = "Licensing is disabled. License uploads are unavailable; change Licensing:Mode and restart to enable licensing.";

    private static readonly FrozenDictionary<string, FeatureDefinition> _features =
        FeatureCatalog.All.ToFrozenDictionary(feature => feature.Key, StringComparer.OrdinalIgnoreCase);

    private readonly LicenseSnapshot _snapshot = CreateSnapshot();

    internal static LicenseSnapshot CreateSnapshot()
    {
        var entitlements = Array.AsReadOnly(FeatureCatalog.All.Select(feature => new Entitlement
        {
            Key = feature.Key,
            Name = feature.DisplayName,
            IsActive = true
        }).ToArray());
        return new LicenseSnapshot(
            HonuaEdition.Enterprise,
            IsValid: true,
            LicenseValidationState.Disabled,
            ExpiresAt: null,
            LicensedTo: null,
            LicenseId: null,
            IssuedAt: null,
            entitlements,
            _features.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SnapshotVersion: 1,
            KeyId: null)
        {
            Mode = LicenseMode.Disabled
        };
    }

    public LicenseSnapshot GetSnapshot() => _snapshot;

    public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementKey);
        _features.TryGetValue(entitlementKey, out var feature);
        var active = _snapshot.HasEntitlement(entitlementKey);
        return new LicenseEntitlementDecision(entitlementKey, active, _snapshot.Edition,
            _snapshot.ValidationState, feature?.MinimumEdition,
            active ? string.Empty : $"Unknown entitlement '{entitlementKey}'.")
        {
            Mode = LicenseMode.Disabled
        };
    }

    public bool IsBlocked => false;

    public CancellationToken OperationCancellation => CancellationToken.None;

    public LicenseStatus GetCurrentStatus() => new(
        _snapshot.Edition, _snapshot.IsValid, null, null, _snapshot.ValidationState,
        Entitlements: _snapshot.Entitlements)
    {
        Mode = LicenseMode.Disabled
    };

    public Task<LicenseInfo> GetLicenseInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new LicenseInfo
        {
            Mode = LicenseMode.Disabled,
            Edition = _snapshot.EditionName,
            IsValid = true,
            ValidationState = _snapshot.ValidationState.ToString(),
            Entitlements = _snapshot.Entitlements
        });

    public Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot.Entitlements);

    public Task<LicenseUploadResult> UploadLicenseAsync(Stream licenseStream, CancellationToken cancellationToken = default)
        => Task.FromResult(new LicenseUploadResult(false, UploadMessage));

    public Task<LicenseInfo> ApplyLicenseAsync(byte[] licenseData, CancellationToken cancellationToken = default)
        => Task.FromException<LicenseInfo>(new LicenseUploadRejectedException(UploadMessage, StatusCodes.Status400BadRequest));
}
