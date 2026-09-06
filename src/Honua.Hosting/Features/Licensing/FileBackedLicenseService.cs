// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Licensing;

internal sealed partial class FileBackedLicenseService :
    IHostedService,
    ILicenseEntitlementService,
    ILicenseStatusProvider,
    ILicenseManager,
    ILicenseOperationPolicy,
    IDisposable
{
    private const int MaxLicenseFileBytes = 64 * 1024;
    private const int ExpectedEnvelopeVersion = 1;
    private const string ExpectedPayloadSchema = "honua.license/v1";

    private static readonly FrozenDictionary<string, FeatureDefinition> FeatureDefinitionsByKey =
        FeatureCatalog.All.ToFrozenDictionary(feature => feature.Key, StringComparer.OrdinalIgnoreCase);

    private readonly IOptions<LicenseOptions> _options;
    private readonly IEd25519Verifier _verifier;
    private readonly ILogger<FileBackedLicenseService> _logger;
    private readonly IReadOnlyList<ILicenseContentSecretResolver> _secretResolvers;
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private LicenseSnapshot _snapshot;
    private long _snapshotVersion;

    public FileBackedLicenseService(
        IOptions<LicenseOptions> options,
        IEd25519Verifier verifier,
        ILogger<FileBackedLicenseService> logger,
        IEnumerable<ILicenseContentSecretResolver>? secretResolvers = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _verifier = verifier;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expiryTimer = _timeProvider.CreateTimer(_ => GetSnapshot(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _secretResolvers = secretResolvers as IReadOnlyList<ILicenseContentSecretResolver>
            ?? secretResolvers?.ToArray()
            ?? [];
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
        EnsureStartupLicense();
        _revalidationTask = RunRevalidationAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_revalidationTask is not null)
        {
            await _revalidationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_runtimeLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stopping.Cancel();
            _expiryTimer.Dispose();
            _operationCancellation.Dispose();
            foreach (var retired in _retiredCancellations)
            {
                retired.Dispose();
            }
            // StopAsync owns asynchronous shutdown. A load may still hold the semaphore.
        }
    }

    public LicenseSnapshot GetSnapshot()
    {
        lock (_runtimeLock)
        {
            var snapshot = _snapshot;
            if (_disposed)
            {
                return snapshot;
            }
            if (snapshot.Edition != HonuaEdition.Community &&
                snapshot.ValidationState == LicenseValidationState.Valid &&
                snapshot.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                snapshot = CreateExpiredSnapshot(snapshot);
                _snapshot = snapshot;
                CancelOperations();
                LicenseRuntimeLog.LicenseExpired(_logger, snapshot.LicenseId, snapshot.ExpiresAt);
            }
            return snapshot;
        }
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
            current.Edition,
            isValid: false,
            LicenseValidationState.Expired,
            NextSnapshotVersion(),
            payload,
            current.KeyId);
    }

    /// <summary>
    /// Computes the bootstrap license snapshot consulted by early startup gates (e.g. the
    /// Redis-cache entitlement probe) before DI is built. When <paramref name="honorDevGrant"/>
    /// is <see langword="true"/> and <c>Licensing:DevGrantEdition</c> is set to a valid edition,
    /// the returned snapshot reflects that dev grant — keeping the startup gate consistent with
    /// the runtime <see cref="DevLicenseEntitlementService"/> entitlement path (honua-server#1787).
    /// Callers must pass <c>honorDevGrant: false</c> in Production so the override never relaxes a
    /// startup gate without a signed license (the host-level guard fails the process closed there).
    /// </summary>
    /// <param name="configuration">The host configuration carrying the <c>Licensing</c> section.</param>
    /// <param name="loggerFactory">Factory for the transient bootstrap logger.</param>
    /// <param name="honorDevGrant">
    /// When <see langword="true"/>, a valid <c>Licensing:DevGrantEdition</c> short-circuits the
    /// snapshot to that edition. Must be <see langword="false"/> in Production.
    /// </param>
    /// <param name="secretResolvers">
    /// The same <see cref="ILicenseContentSecretResolver"/> set that <c>AddHonuaLicensing</c> registers
    /// for the per-request license service (e.g. AWS Secrets Manager and/or Azure Key Vault). Supplying
    /// them here lets the bootstrap snapshot honor <c>Licensing:LicenseContentSecretRef</c> (e.g. a
    /// secret-store-only Pro license); without them a paid secret-ref-only deployment refuses startup
    /// (honua-server#1755). It is optional so hosts without a secret resolver (or built with the cloud
    /// SDK excluded) still resolve file / inline / Community licenses correctly.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the snapshot load.</param>
    internal static async Task<LicenseSnapshot> LoadBootstrapSnapshotAsync(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        bool honorDevGrant = false,
        IEnumerable<ILicenseContentSecretResolver>? secretResolvers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var options = new LicenseOptions();
        configuration.GetSection(LicenseOptions.SectionName).Bind(options);

        if (honorDevGrant &&
            DevLicenseSnapshotFactory.TryParseEdition(options.DevGrantEdition, out var grantEdition))
        {
            return DevLicenseSnapshotFactory.Create(grantEdition);
        }

        using var service = new FileBackedLicenseService(
            Options.Create(options),
            new BouncyCastleEd25519Verifier(),
            loggerFactory.CreateLogger<FileBackedLicenseService>(),
            secretResolvers);
        service._suppressExpiryWarnings = true;
        await service.LoadConfiguredLicenseAsync(cancellationToken).ConfigureAwait(false);
        service.EnsureStartupLicense();
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
        if (options.Edition == HonuaEdition.Community)
        {
            PublishCommunity(LicenseValidationState.NoLicenseConfigured, isValid: true, payload: null, keyId: null);
            return;
        }

        // A successful admin upload is an explicit persisted override. Check it before
        // contacting a secret store, including when subsequent uploads have been disabled.
        if (!string.IsNullOrWhiteSpace(options.LicensePath)
            && await TryLoadUploadedOverrideAsync(options, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Without an uploaded override, a secret-store reference takes highest precedence:
        // the ~2KB signed envelope is fetched from a secret manager (e.g. AWS Secrets Manager) at startup so it does not
        // have to fit a serverless environment-variable size limit or be baked into the image.
        // Resolution records source failures. Strict startup validation rejects a paid
        // deployment when no valid configured source can be loaded.
        var secretContent = await TryResolveLicenseContentSecretAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(secretContent))
        {
            ApplyInlineLicenseContent(secretContent, options);
            return;
        }

        // Inline content takes precedence over a file path so the license can be
        // delivered on a read-only/serverless filesystem (e.g. AWS Lambda).
        if (!string.IsNullOrWhiteSpace(options.LicenseContent))
        {
            ApplyInlineLicenseContent(options.LicenseContent, options);
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
            await ReadAndPublishLicenseFileAsync(options.LicensePath, options, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> TryLoadUploadedOverrideAsync(LicenseOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await ReadAndPublishLicenseFileAsync(options.LicensePath + ".uploaded", options, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable/malformed override must not silently resurrect a stale source.
            PublishCommunity(LicenseValidationState.Malformed, isValid: false, payload: null, keyId: null);
            LicenseRuntimeLog.LicenseMalformed(_logger, ex.GetType().Name);
            return true;
        }
    }

    private async Task ReadAndPublishLicenseFileAsync(string path, LicenseOptions options, CancellationToken cancellationToken)
    {
        await using var licenseStream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var licenseData = await ReadBoundedLicenseDataAsync(licenseStream, cancellationToken).ConfigureAwait(false);
        var result = ValidateLicenseData(licenseData, options);
        PublishSnapshot(result.Snapshot);
        LogValidationResult(result);
    }

    /// <summary>
    /// Resolves <see cref="LicenseOptions.LicenseContentSecretRef"/> through the first registered
    /// <see cref="ILicenseContentSecretResolver"/> that recognizes the reference (e.g. AWS Secrets
    /// Manager for <c>aws:secretsmanager:</c>, Azure Key Vault for <c>azure:keyvault:</c>). Never
    /// throws: no registered resolver, an unsupported reference, or an unreachable secret returns
    /// <c>null</c> so the caller falls through to inline content / file validation.
    /// </summary>
    private async Task<string?> TryResolveLicenseContentSecretAsync(
        LicenseOptions options,
        CancellationToken cancellationToken)
    {
        var secretRef = options.LicenseContentSecretRef;
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return null;
        }

        if (_secretResolvers.Count == 0)
        {
            LicenseRuntimeLog.LicenseSecretResolverUnavailable(_logger);
            return null;
        }

        var resolver = _secretResolvers.FirstOrDefault(candidate => candidate.CanResolve(secretRef));

        if (resolver is null)
        {
            LicenseRuntimeLog.LicenseSecretReferenceUnsupported(_logger);
            return null;
        }

        try
        {
            var content = await resolver
                .ResolveLicenseContentAsync(secretRef, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                LicenseRuntimeLog.LicenseSecretResolutionEmpty(_logger);
                return null;
            }

            LicenseRuntimeLog.LicenseLoadedFromSecret(_logger);
            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LicenseRuntimeLog.LicenseSecretResolutionFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Validates an in-memory signed-license envelope (UTF-8 JSON, from inline config or a
    /// resolved secret) and publishes the resulting snapshot. Malformed content publishes a
    /// failed snapshot; the startup contract rejects it on paid deployments.
    /// </summary>
    private void ApplyInlineLicenseContent(string licenseContent, LicenseOptions options)
    {
        try
        {
            var inlineData = System.Text.Encoding.UTF8.GetBytes(licenseContent);
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
    }

    private async Task<LicenseUploadResult> ApplyUploadedLicenseDataAsync(
        byte[] licenseData,
        CancellationToken cancellationToken)
    {
        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyUploadedLicenseDataCoreAsync(licenseData, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private async Task<LicenseUploadResult> ApplyUploadedLicenseDataCoreAsync(
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

            // This atomic replacement is the commit point. A failed upload must never
            // change the fallback file before the authoritative override is durable.
            await WriteLicenseFileAsync(options.LicensePath + ".uploaded", licenseData, cancellationToken).ConfigureAwait(false);

            PublishSnapshot(result.Snapshot);
            LogValidationResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LicenseRuntimeLog.LicenseUploadSaveFailed(_logger, ex);
            return new LicenseUploadResult(false, "License upload could not be saved. See server logs for details.");
        }

        // Retain the configured file for existing operator tooling. Once committed,
        // cancellation or a mirror failure cannot turn the applied upload into a rejection.
        try
        {
            await WriteLicenseFileAsync(options.LicensePath, licenseData, CancellationToken.None).ConfigureAwait(false);
            return new LicenseUploadResult(true, "License applied.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LicenseRuntimeLog.LicenseUploadSaveFailed(_logger, ex);
            return new LicenseUploadResult(true,
                "License applied to the persisted upload override; the LicensePath mirror could not be updated. See server logs for details.");
        }
    }

    private static async Task WriteLicenseFileAsync(string path, byte[] licenseData, CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, licenseData, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
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
            // Intentional: best-effort temp-file cleanup after a failed upload;
            // an orphaned temp file is harmless and must not mask the original error.
        }
        catch (UnauthorizedAccessException)
        {
            // Intentional: best-effort temp-file cleanup after a failed upload;
            // an orphaned temp file is harmless and must not mask the original error.
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

        if (payload.ExpiresAt.HasValue && payload.ExpiresAt.Value <= _timeProvider.GetUtcNow())
        {
            return CreateInvalidResult(LicenseValidationState.Expired, "expired", payload, envelope.KeyId, edition);
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
        [NotNullWhen(true)] SignedLicensePayload? payload,
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
        if (Enum.TryParse(value, ignoreCase: true, out edition) && Enum.IsDefined(edition))
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
        string? keyId,
        HonuaEdition edition = HonuaEdition.Community)
    {
        var snapshot = state == LicenseValidationState.Expired && payload is not null
            ? CreateSnapshot(
                edition,
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
            .Where(feature => (isValid || edition == HonuaEdition.Community) && feature.MinimumEdition == HonuaEdition.Community)
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
        lock (_runtimeLock)
        {
            if (_disposed)
            {
                return;
            }
            var expected = _options.Value.Edition ?? (_snapshot.Edition > HonuaEdition.Community
                ? _snapshot.Edition : snapshot.Edition);
            if (expected > HonuaEdition.Community &&
                (snapshot.ValidationState != LicenseValidationState.Valid || snapshot.Edition < expected))
            {
                snapshot = snapshot with
                {
                    Edition = expected,
                    IsValid = false,
                    ValidationState = snapshot.ValidationState == LicenseValidationState.Valid
                        ? LicenseValidationState.Malformed : snapshot.ValidationState,
                    ActiveEntitlementKeys = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                    Entitlements = snapshot.Entitlements.Select(item => new Entitlement
                    {
                        Key = item.Key,
                        Name = item.Name,
                        IsActive = false
                    }).ToArray()
                };
            }
            _snapshot = snapshot;
            var blocked = snapshot.Edition > HonuaEdition.Community && !snapshot.IsValid;
            if (blocked)
            {
                CancelOperations();
            }
            else if (_operationCancellation.IsCancellationRequested)
            {
                _retiredCancellations.Add(_operationCancellation);
                _operationCancellation = new CancellationTokenSource();
            }

            var due = !blocked && snapshot.Edition > HonuaEdition.Community && snapshot.ExpiresAt.HasValue
                ? snapshot.ExpiresAt.Value - _timeProvider.GetUtcNow() : Timeout.InfiniteTimeSpan;
            if (due != Timeout.InfiniteTimeSpan)
            {
                due = TimeSpan.FromMilliseconds(Math.Clamp(due.TotalMilliseconds, 0, uint.MaxValue - 1));
            }
            _expiryTimer.Change(due, Timeout.InfiniteTimeSpan);
            LogExpiryWarning(snapshot);
        }
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
