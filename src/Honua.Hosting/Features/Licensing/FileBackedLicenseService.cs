// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Licensing;

internal sealed class FileBackedLicenseService :
    IHostedService,
    ILicenseEntitlementService,
    ILicenseStatusProvider,
    ILicenseManager
{
    private const int MaxLicenseFileBytes = 64 * 1024;
    private const int ExpectedEnvelopeVersion = 1;
    private const string ExpectedPayloadSchema = "honua.license/v1";

    private static readonly FrozenDictionary<string, FeatureDefinition> FeatureDefinitionsByKey =
        FeatureCatalog.All.ToFrozenDictionary(feature => feature.Key, StringComparer.OrdinalIgnoreCase);

    private readonly IOptions<LicenseOptions> _options;
    private readonly IEd25519Verifier _verifier;
    private readonly ILogger<FileBackedLicenseService> _logger;
    private readonly ILicenseContentSecretResolver? _licenseContentSecretResolver;
    private LicenseSnapshot _snapshot;
    private long _snapshotVersion;

    public FileBackedLicenseService(
        IOptions<LicenseOptions> options,
        IEd25519Verifier verifier,
        ILogger<FileBackedLicenseService> logger,
        ILicenseContentSecretResolver? licenseContentSecretResolver = null)
    {
        _options = options;
        _verifier = verifier;
        _logger = logger;
        _licenseContentSecretResolver = licenseContentSecretResolver;
        _snapshot = CreateCommunitySnapshot(
            LicenseValidationState.NoLicenseConfigured,
            isValid: true,
            snapshotVersion: 0,
            payload: null,
            keyId: null);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadConfiguredLicenseAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public LicenseSnapshot GetSnapshot()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.ValidationState != LicenseValidationState.Valid ||
            !snapshot.ExpiresAt.HasValue ||
            snapshot.ExpiresAt.Value > DateTimeOffset.UtcNow)
        {
            return snapshot;
        }

        // The license expired while the server was running. Republish an expired
        // snapshot so entitlement checks and health reporting reflect reality without
        // requiring a restart.
        var expired = CreateExpiredSnapshot(snapshot);
        if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, expired, snapshot), snapshot))
        {
            LicenseRuntimeLog.LicenseExpired(_logger, snapshot.LicenseId, snapshot.ExpiresAt);
            return expired;
        }

        // Another thread published a newer snapshot concurrently; use that one.
        return Volatile.Read(ref _snapshot);
    }

    private LicenseSnapshot CreateExpiredSnapshot(LicenseSnapshot current)
    {
        var payload = new SignedLicensePayload
        {
            LicenseId = current.LicenseId,
            LicensedTo = current.LicensedTo,
            IssuedAt = current.IssuedAt,
            ExpiresAt = current.ExpiresAt
        };

        return CreateSnapshot(
            HonuaEdition.Community,
            isValid: false,
            LicenseValidationState.Expired,
            NextSnapshotVersion(),
            payload,
            current.KeyId);
    }

    internal static async Task<LicenseSnapshot> LoadBootstrapSnapshotAsync(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var options = new LicenseOptions();
        configuration.GetSection(LicenseOptions.SectionName).Bind(options);

        var service = new FileBackedLicenseService(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            loggerFactory.CreateLogger<FileBackedLicenseService>());
        await service.StartAsync(cancellationToken).ConfigureAwait(false);
        return service.GetSnapshot();
    }

    public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementKey);

        var snapshot = GetSnapshot();
        var isActive = snapshot.HasEntitlement(entitlementKey);
        FeatureDefinitionsByKey.TryGetValue(entitlementKey, out var definition);
        var requiredEdition = definition?.MinimumEdition;

        var upgradeMessage = isActive
            ? string.Empty
            : BuildUpgradeMessage(entitlementKey, definition, snapshot);

        return new LicenseEntitlementDecision(
            entitlementKey,
            isActive,
            snapshot.Edition,
            snapshot.ValidationState,
            requiredEdition,
            upgradeMessage);
    }

    public LicenseStatus GetCurrentStatus()
    {
        var snapshot = GetSnapshot();
        return new LicenseStatus(
            snapshot.Edition,
            snapshot.IsValid,
            snapshot.ExpiresAt,
            snapshot.LicensedTo,
            snapshot.ValidationState,
            snapshot.LicenseId,
            snapshot.IssuedAt,
            snapshot.Entitlements,
            snapshot.CapacityTerms);
    }

    public async Task<LicenseUploadResult> UploadLicenseAsync(
        Stream licenseStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseStream);

        var licenseData = await ReadBoundedLicenseDataAsync(licenseStream, cancellationToken).ConfigureAwait(false);
        return await ApplyUploadedLicenseDataAsync(licenseData, cancellationToken).ConfigureAwait(false);
    }

    public Task<LicenseInfo> GetLicenseInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ToLicenseInfo(GetSnapshot()));

    public Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(GetSnapshot().Entitlements);

    public async Task<LicenseInfo> ApplyLicenseAsync(
        byte[] licenseData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(licenseData);

        var result = await ApplyUploadedLicenseDataAsync(licenseData, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new LicenseUploadRejectedException(result.Message, StatusCodes.Status400BadRequest);
        }

        return ToLicenseInfo(GetSnapshot());
    }

    internal static LicenseHealthSummary ToHealthSummary(LicenseSnapshot snapshot)
    {
        var status = new LicenseStatus(
            snapshot.Edition,
            snapshot.IsValid,
            snapshot.ExpiresAt,
            snapshot.LicensedTo,
            snapshot.ValidationState,
            snapshot.LicenseId,
            snapshot.IssuedAt,
            snapshot.Entitlements,
            snapshot.CapacityTerms);

        var activeEntitlements = snapshot.Entitlements
            .Where(entitlement => entitlement.IsActive)
            .Select(entitlement => entitlement.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LicenseHealthSummary
        {
            Edition = snapshot.Edition.ToString(),
            ValidationState = snapshot.ValidationState.ToString(),
            IsValid = snapshot.IsValid,
            ExpiresAt = snapshot.ExpiresAt,
            DaysUntilExpiry = status.DaysUntilExpiry,
            LicenseId = snapshot.LicenseId,
            LicensedTo = snapshot.LicensedTo,
            ActiveEntitlements = activeEntitlements
        };
    }

    private async Task LoadConfiguredLicenseAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        // Resolve a Licensing:LicenseContentSecretRef (e.g.
        // azure:keyvault:https://<vault>.vault.azure.net/<secret>) into the envelope JSON via the
        // optional ILicenseContentSecretResolver. The resolved value is treated exactly like inline
        // LicenseContent. Explicit inline LicenseContent always wins. FAIL-SAFE: any resolver error
        // falls back to Community and never crashes the host.
        // PROVISIONAL (#1745) pending the canonical resolver seam in honua-server#1742.
        var effectiveLicenseContent = options.LicenseContent;
        if (string.IsNullOrWhiteSpace(effectiveLicenseContent)
            && !string.IsNullOrWhiteSpace(options.LicenseContentSecretRef))
        {
            if (_licenseContentSecretResolver is not null
                && _licenseContentSecretResolver.CanResolve(options.LicenseContentSecretRef))
            {
                try
                {
                    effectiveLicenseContent = await _licenseContentSecretResolver
                        .ResolveLicenseContentAsync(options.LicenseContentSecretRef, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail safe to Community — a secret-store outage must not crash the host.
                    PublishCommunity(LicenseValidationState.NoLicenseConfigured, isValid: true, payload: null, keyId: null);
                    LicenseRuntimeLog.LicenseMalformed(_logger, $"secret-ref-resolve:{ex.GetType().Name}");
                    return;
                }
            }
            else
            {
                // No resolver can handle the configured ref (e.g. Azure module not compiled in).
                PublishCommunity(LicenseValidationState.NoLicenseConfigured, isValid: true, payload: null, keyId: null);
                LicenseRuntimeLog.NoLicensePathConfigured(_logger);
                return;
            }
        }

        // Inline content takes precedence over a file path so the license can be
        // delivered on a read-only/serverless filesystem (e.g. AWS Lambda), typically
        // via a Licensing:LicenseContent=aws:secretsmanager:<arn> secret reference.
        if (!string.IsNullOrWhiteSpace(effectiveLicenseContent))
        {
            try
            {
                var inlineData = System.Text.Encoding.UTF8.GetBytes(effectiveLicenseContent);
                var inlineResult = ValidateLicenseData(inlineData, options);
                PublishSnapshot(inlineResult.Snapshot);
                LogValidationResult(inlineResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var snapshot = CreateCommunitySnapshot(
                    LicenseValidationState.Malformed,
                    isValid: false,
                    NextSnapshotVersion(),
                    payload: null,
                    keyId: null);
                PublishSnapshot(snapshot);
                LicenseRuntimeLog.LicenseMalformed(_logger, ex.GetType().Name);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(options.LicensePath))
        {
            PublishCommunity(LicenseValidationState.NoLicenseConfigured, isValid: true, payload: null, keyId: null);
            LicenseRuntimeLog.NoLicensePathConfigured(_logger);
            return;
        }

        if (!File.Exists(options.LicensePath))
        {
            PublishCommunity(LicenseValidationState.MissingFile, isValid: false, payload: null, keyId: null);
            LicenseRuntimeLog.LicenseFileMissing(_logger, options.LicensePath);
            return;
        }

        try
        {
            await using var licenseStream = new FileStream(
                options.LicensePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var licenseData = await ReadBoundedLicenseDataAsync(licenseStream, cancellationToken).ConfigureAwait(false);
            var result = ValidateLicenseData(licenseData, options);
            PublishSnapshot(result.Snapshot);
            LogValidationResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var snapshot = CreateCommunitySnapshot(
                LicenseValidationState.Malformed,
                isValid: false,
                NextSnapshotVersion(),
                payload: null,
                keyId: null);
            PublishSnapshot(snapshot);
            LicenseRuntimeLog.LicenseMalformed(_logger, ex.GetType().Name);
        }
    }

    private async Task<LicenseUploadResult> ApplyUploadedLicenseDataAsync(
        byte[] licenseData,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!options.AllowAdminUpload)
        {
            const string message = "License upload is disabled. Set Licensing:AllowAdminUpload=true to enable admin uploads.";
            LicenseRuntimeLog.LicenseUploadRejected(_logger, message);
            return new LicenseUploadResult(false, message);
        }

        if (string.IsNullOrWhiteSpace(options.LicensePath))
        {
            const string message = "License upload requires Licensing:LicensePath to be configured.";
            LicenseRuntimeLog.LicenseUploadRejected(_logger, message);
            return new LicenseUploadResult(false, message);
        }

        if (licenseData.Length == 0)
        {
            const string message = "License data cannot be empty.";
            LicenseRuntimeLog.LicenseUploadRejected(_logger, message);
            return new LicenseUploadResult(false, message);
        }

        var result = ValidateLicenseData(licenseData, options);
        if (result.Snapshot.ValidationState != LicenseValidationState.Valid)
        {
            var message = $"License validation failed: {result.Snapshot.ValidationState}.";
            LicenseRuntimeLog.LicenseUploadRejected(_logger, message);
            return new LicenseUploadResult(false, message);
        }

        try
        {
            var directory = Path.GetDirectoryName(options.LicensePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Unique temp file per upload so concurrent admin uploads cannot interleave
            // writes/moves on the same temporary path.
            var tempPath = $"{options.LicensePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, licenseData, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, options.LicensePath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            PublishSnapshot(result.Snapshot);
            LogValidationResult(result);
            return new LicenseUploadResult(true, "License applied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LicenseRuntimeLog.LicenseUploadSaveFailed(_logger, ex);
            return new LicenseUploadResult(false, "License upload could not be saved. See server logs for details.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private LicenseValidationResult ValidateLicenseData(byte[] licenseData, LicenseOptions options)
    {
        if (licenseData.Length == 0 || licenseData.Length > MaxLicenseFileBytes)
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-size", null, null);
        }

        SignedLicenseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                licenseData,
                LicenseFileJsonContext.Default.SignedLicenseEnvelope);
        }
        catch (JsonException)
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-envelope-json", null, null);
        }

        if (envelope is null ||
            envelope.Version != ExpectedEnvelopeVersion ||
            string.IsNullOrWhiteSpace(envelope.KeyId) ||
            string.IsNullOrWhiteSpace(envelope.Payload) ||
            string.IsNullOrWhiteSpace(envelope.Signature))
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-envelope", null, envelope?.KeyId);
        }

        if (!options.TrustedKeys.TryGetValue(envelope.KeyId, out var configuredPublicKey))
        {
            return CreateInvalidResult(LicenseValidationState.UnknownKey, "unknown-key", null, envelope.KeyId);
        }

        if (!TryDecodeKey(configuredPublicKey, out var publicKey) || publicKey.Length != 32)
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-configured-public-key", null, envelope.KeyId);
        }

        if (!TryDecodeBase64Url(envelope.Payload, out var payloadBytes) ||
            !TryDecodeBase64Url(envelope.Signature, out var signatureBytes) ||
            signatureBytes.Length != 64)
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-base64url", null, envelope.KeyId);
        }

        if (!_verifier.Verify(publicKey, payloadBytes, signatureBytes))
        {
            return CreateInvalidResult(LicenseValidationState.InvalidSignature, "invalid-signature", null, envelope.KeyId);
        }

        SignedLicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                payloadBytes,
                LicenseFileJsonContext.Default.SignedLicensePayload);
        }
        catch (JsonException)
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, "invalid-payload-json", null, envelope.KeyId);
        }

        if (!TryValidatePayload(payload, out var edition, out var payloadError))
        {
            return CreateInvalidResult(LicenseValidationState.Malformed, payloadError, payload, envelope.KeyId);
        }

        if (payload!.ExpiresAt.HasValue && payload.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return CreateInvalidResult(LicenseValidationState.Expired, "expired", payload, envelope.KeyId);
        }

        var snapshot = CreateSnapshot(
            edition,
            isValid: true,
            LicenseValidationState.Valid,
            NextSnapshotVersion(),
            payload,
            envelope.KeyId);

        return new LicenseValidationResult(snapshot, "valid");
    }

    private static bool TryValidatePayload(
        SignedLicensePayload? payload,
        out HonuaEdition edition,
        out string reason)
    {
        edition = HonuaEdition.Community;
        reason = string.Empty;

        if (payload is null)
        {
            reason = "missing-payload";
            return false;
        }

        if (!string.Equals(payload.Schema, ExpectedPayloadSchema, StringComparison.Ordinal))
        {
            reason = "unsupported-schema";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.LicenseId))
        {
            reason = "missing-license-id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.LicensedTo))
        {
            reason = "missing-licensee";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.Edition) ||
            !TryParseEdition(payload.Edition, out edition))
        {
            reason = "invalid-edition";
            return false;
        }

        if (!payload.IssuedAt.HasValue)
        {
            reason = "missing-issued-at";
            return false;
        }

        if (payload.Capacity is not null &&
            !TryValidateCapacityTerms(payload.Capacity, out reason))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateCapacityTerms(LicenseCapacityTerms capacity, out string reason)
    {
        reason = string.Empty;

        if (capacity.MaxSustainedServingUnits <= 0m)
        {
            reason = "invalid-capacity-band";
            return false;
        }

        if (capacity.AnnualSurgeDays is < 0)
        {
            reason = "invalid-surge-days";
            return false;
        }

        if (string.IsNullOrWhiteSpace(capacity.SurgeAllowance))
        {
            reason = "invalid-surge-allowance";
            return false;
        }

        return true;
    }

    private static bool TryParseEdition(string value, out HonuaEdition edition)
    {
        if (Enum.TryParse(value, ignoreCase: true, out edition))
        {
            return true;
        }

        if (string.Equals(value, "professional", StringComparison.OrdinalIgnoreCase))
        {
            edition = HonuaEdition.Pro;
            return true;
        }

        return false;
    }

    private LicenseValidationResult CreateInvalidResult(
        LicenseValidationState state,
        string reason,
        SignedLicensePayload? payload,
        string? keyId)
    {
        var snapshot = state == LicenseValidationState.Expired && payload is not null
            ? CreateSnapshot(
                HonuaEdition.Community,
                isValid: false,
                state,
                NextSnapshotVersion(),
                payload,
                keyId)
            : CreateCommunitySnapshot(state, isValid: false, NextSnapshotVersion(), payload, keyId);

        return new LicenseValidationResult(snapshot, reason);
    }

    private static LicenseSnapshot CreateCommunitySnapshot(
        LicenseValidationState validationState,
        bool isValid,
        long snapshotVersion,
        SignedLicensePayload? payload,
        string? keyId)
        => CreateSnapshot(
            HonuaEdition.Community,
            isValid,
            validationState,
            snapshotVersion,
            payload,
            keyId);

    private static LicenseSnapshot CreateSnapshot(
        HonuaEdition edition,
        bool isValid,
        LicenseValidationState validationState,
        long snapshotVersion,
        SignedLicensePayload? payload,
        string? keyId)
    {
        var signedEntitlements = isValid && payload?.Entitlements is { Length: > 0 }
            ? payload.Entitlements
            : [];

        var knownSignedEntitlements = signedEntitlements
            .Where(key => FeatureDefinitionsByKey.ContainsKey(key))
            .ToArray();

        var activeKeys = FeatureCatalog.All
            .Where(feature => feature.MinimumEdition == HonuaEdition.Community)
            .Select(feature => feature.Key)
            .Concat(knownSignedEntitlements)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        var entitlements = FeatureCatalog.All
            .Select(feature => new Entitlement
            {
                Key = feature.Key,
                Name = feature.DisplayName,
                IsActive = activeKeys.Contains(feature.Key)
            })
            .ToArray();

        var capacityTerms = isValid ? payload?.Capacity : null;

        return new LicenseSnapshot(
            edition,
            isValid,
            validationState,
            payload?.ExpiresAt,
            payload?.LicensedTo,
            payload?.LicenseId,
            payload?.IssuedAt,
            entitlements,
            activeKeys,
            snapshotVersion,
            keyId,
            capacityTerms);
    }

    private void PublishCommunity(
        LicenseValidationState state,
        bool isValid,
        SignedLicensePayload? payload,
        string? keyId)
    {
        var snapshot = CreateCommunitySnapshot(
            state,
            isValid,
            NextSnapshotVersion(),
            payload,
            keyId);
        PublishSnapshot(snapshot);
    }

    private void PublishSnapshot(LicenseSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
    }

    private long NextSnapshotVersion() => Interlocked.Increment(ref _snapshotVersion);

    private void LogValidationResult(LicenseValidationResult result)
    {
        var snapshot = result.Snapshot;
        switch (snapshot.ValidationState)
        {
            case LicenseValidationState.Valid:
                LicenseRuntimeLog.LicenseLoaded(
                    _logger,
                    snapshot.LicenseId,
                    snapshot.Edition,
                    snapshot.KeyId ?? string.Empty,
                    snapshot.ExpiresAt,
                    snapshot.ActiveEntitlementKeys.Count);
                break;
            case LicenseValidationState.Malformed:
                LicenseRuntimeLog.LicenseMalformed(_logger, result.Reason);
                break;
            case LicenseValidationState.UnknownKey:
                LicenseRuntimeLog.UnknownKey(_logger, snapshot.KeyId ?? string.Empty);
                break;
            case LicenseValidationState.InvalidSignature:
                LicenseRuntimeLog.InvalidSignature(_logger, snapshot.KeyId ?? string.Empty);
                break;
            case LicenseValidationState.Expired:
                LicenseRuntimeLog.LicenseExpired(_logger, snapshot.LicenseId, snapshot.ExpiresAt);
                break;
        }
    }

    private static LicenseInfo ToLicenseInfo(LicenseSnapshot snapshot)
    {
        return new LicenseInfo
        {
            Edition = snapshot.Edition.ToString(),
            ExpiresAt = snapshot.ExpiresAt,
            IsValid = snapshot.IsValid,
            ValidationState = snapshot.ValidationState.ToString(),
            LicensedTo = snapshot.LicensedTo,
            LicenseId = snapshot.LicenseId,
            IssuedAt = snapshot.IssuedAt,
            Entitlements = snapshot.Entitlements,
            CapacityTerms = snapshot.CapacityTerms
        };
    }

    private static string BuildUpgradeMessage(
        string entitlementKey,
        FeatureDefinition? definition,
        LicenseSnapshot snapshot)
    {
        var featureName = definition?.DisplayName ?? entitlementKey;
        var requiredEdition = definition?.MinimumEdition.ToString() ?? "a paid edition";
        return $"{featureName} requires an active {requiredEdition} entitlement. " +
            $"Current edition is {snapshot.Edition}; install a license that includes '{entitlementKey}'.";
    }

    private static async Task<byte[]> ReadBoundedLicenseDataAsync(
        Stream licenseStream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(MaxLicenseFileBytes + 1);
        var scratch = new byte[4096];

        while (buffer.Length <= MaxLicenseFileBytes)
        {
            var bytesRemaining = MaxLicenseFileBytes + 1 - buffer.Length;
            var bytesToRead = (int)Math.Min(scratch.Length, bytesRemaining);
            var bytesRead = await licenseStream
                .ReadAsync(scratch.AsMemory(0, bytesToRead), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            buffer.Write(scratch, 0, bytesRead);
        }

        return buffer.ToArray();
    }

    private static bool TryDecodeKey(string value, out byte[] bytes)
    {
        var key = value.Trim();
        const string base64Prefix = "base64:";
        if (key.StartsWith(base64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes = Convert.FromBase64String(key[base64Prefix.Length..]);
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }

        return TryDecodeBase64Url(key, out bytes);
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        var normalized = value.Trim();
        const string base64UrlPrefix = "base64url:";
        if (normalized.StartsWith(base64UrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[base64UrlPrefix.Length..];
        }

        normalized = normalized.Replace('-', '+').Replace('_', '/');
        var padding = (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => null
        };
        if (padding is null)
        {
            bytes = [];
            return false;
        }

        normalized += padding;

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private readonly record struct LicenseValidationResult(LicenseSnapshot Snapshot, string Reason);
}
