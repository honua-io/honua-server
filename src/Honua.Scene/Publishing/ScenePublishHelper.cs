// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Infrastructure.Scene;

internal static class ScenePublishHelper
{
    internal const int DefaultCacheMaxAgeSeconds = 3600;

    internal static string ResolveSceneId(string? explicitId, string prefix, string intentId)
    {
        if (!string.IsNullOrEmpty(explicitId))
        {
            var normalized = explicitId.ToLowerInvariant();
            if (!SceneDatasetValidator.TryValidateSceneId(normalized, out var error))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: sceneId is invalid: {error}");
            }

            return normalized;
        }

        var suffix = intentId.Length >= 8 ? intentId[..8] : intentId;
        var slugBudget = Math.Max(1, SceneDatasetValidator.MaxSceneIdLength - 1 - suffix.Length);
        var slug = Slugify(prefix, slugBudget);
        var candidate = $"{slug}-{suffix}".ToLowerInvariant();
        if (!SceneDatasetValidator.TryValidateSceneId(candidate, out var autoError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: derived sceneId '{candidate}' is invalid: {autoError}. Supply an explicit sceneId.");
        }

        return candidate;
    }

    internal static string ResolveDisplayName(string? explicitName, string defaultBaseName, string sceneId)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var suffix = $" ({sceneId})";
        var available = SceneDatasetValidator.MaxSceneNameLength - suffix.Length;
        if (available <= 0)
        {
            return sceneId;
        }

        var baseName = defaultBaseName.Length > available ? defaultBaseName[..available] : defaultBaseName;
        return baseName + suffix;
    }

    internal static void ValidateCommonOptions(
        string displayName,
        string? tilesetFileName,
        string? editionGate,
        int? cacheMaxAgeSeconds,
        SceneExtent? extent,
        bool requiresAuth)
    {
        if (!SceneDatasetValidator.TryValidateName(displayName, out var nameError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: displayName is invalid: {nameError}");
        }

        if (!SceneDatasetValidator.TryValidateTilesetFileName(tilesetFileName, out var tilesetError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: tilesetFileName is invalid: {tilesetError}");
        }

        if (!SceneDatasetValidator.TryValidateEditionGate(editionGate, out var editionError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: editionGate is invalid: {editionError}");
        }

        if (cacheMaxAgeSeconds is { } cache)
        {
            var policy = new SceneCachePolicy(cache, NoStore: false);
            if (!SceneDatasetValidator.TryValidateCachePolicy(policy, out var cacheError))
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.OptionsInvalid}: cacheMaxAgeSeconds is invalid: {cacheError}");
            }
        }

        if (!SceneDatasetValidator.TryValidateExtent(extent, out var extentError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: extent is invalid: {extentError}");
        }

        if (!SceneDatasetValidator.TryValidateAccessFlags(!requiresAuth, requiresAuth, out var accessError))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: access flags are invalid: {accessError}");
        }
    }

    internal static async Task PreflightConflictAsync(
        ISceneRegistrationService? registration,
        string sceneId,
        CancellationToken cancellationToken)
    {
        if (registration is null)
        {
            return;
        }

        var existing = await registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.SceneRegistrationConflict}: A scene dataset with id '{sceneId}' already exists.");
        }
    }

    internal static async Task UploadRegisterAndPromoteAsync(
        ScenePublishCommitContext commit,
        CancellationToken cancellationToken)
    {
        var newDatasetId = Guid.NewGuid();
        var storagePrefix = await SceneAssetStorageUploader.UploadTreeAsync(
            commit.Storage, commit.StagingDirectory, commit.SceneId, cancellationToken, newDatasetId).ConfigureAwait(false);
        if (storagePrefix is not null)
        {
            await SceneOutputGuard.RunAsync(() => SceneAssetHydration.WriteMarkerAsync(
                commit.StagingDirectory,
                SceneAssetHydration.BuildToken(newDatasetId, storagePrefix),
                cancellationToken)).ConfigureAwait(false);
        }

        Guid? registeredDatasetId = null;
        if (commit.Registration is not null)
        {
            var record = new SceneDatasetRecord
            {
                DatasetId = newDatasetId,
                Id = commit.SceneId,
                Name = commit.DisplayName,
                Description = commit.Description,
                AssetRoot = commit.OutputDirectory,
                AssetStoragePrefix = storagePrefix,
                TilesetFileName = commit.TilesetFileName,
                DatasetType = commit.DatasetType,
                Extent = commit.Extent,
                Crs = commit.Crs,
                CachePolicy = new SceneCachePolicy(commit.CacheMaxAgeSeconds, NoStore: false),
                EditionGate = commit.EditionGate,
                RequiresAuth = commit.RequiresAuth,
                IsPublic = !commit.RequiresAuth,
                AllowedRoles = null,
                Status = SceneDatasetStatus.Active,
                ValidationMessage = null,
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = commit.CreatedBy,
                UpdatedAt = null,
            };

            try
            {
                var saved = await commit.Registration.RegisterAsync(record, cancellationToken).ConfigureAwait(false);
                registeredDatasetId = saved?.DatasetId is { } id && id != Guid.Empty ? id : record.DatasetId;
            }
            catch (SceneDatasetAlreadyExistsException ex)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.SceneRegistrationConflict}: A scene dataset with id '{commit.SceneId}' or name '{commit.DisplayName}' already exists.",
                    ex);
            }
        }

        try
        {
            SceneOutputGuard.Run(() => SceneTilesetStaging.PromoteStagingToFinal(
                commit.StagingDirectory,
                commit.OutputDirectory,
                commit.OnOverwroteStaleFinalDir));
        }
        catch
        {
            if (registeredDatasetId is { } datasetId && commit.Registration is not null)
            {
                try
                {
                    var deactivated = await commit.Registration
                        .DeactivateAsync(datasetId, CancellationToken.None)
                        .ConfigureAwait(false);
                    commit.OnRegistrationCompensated?.Invoke(datasetId, deactivated);
                }
                // Compensation boundary: preserve the original promotion error while reporting rollback failures.
                catch (Exception compensationEx) when (compensationEx is not OutOfMemoryException)
                {
                    commit.OnRegistrationCompensationFailed?.Invoke(datasetId, compensationEx);
                }
            }

            throw;
        }
    }

    internal static string Slugify(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
        {
            return "scene";
        }

        var span = value.AsSpan();
        var capacity = Math.Min(span.Length, maxLength);
        var buffer = new char[capacity];
        var written = 0;
        var lastDash = false;
        for (var i = 0; i < span.Length && written < capacity; i++)
        {
            var c = span[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[written++] = char.ToLowerInvariant(c);
                lastDash = false;
            }
            else if (!lastDash && written > 0)
            {
                buffer[written++] = '-';
                lastDash = true;
            }
        }

        if (written == 0)
        {
            return "scene";
        }

        if (lastDash)
        {
            written--;
        }

        return new string(buffer, 0, written);
    }
}

internal sealed record ScenePublishCommitContext(
    ISceneRegistrationService? Registration,
    ICloudFileStorage? Storage,
    string SceneId,
    string DisplayName,
    string? Description,
    string StagingDirectory,
    string OutputDirectory,
    string TilesetFileName,
    SceneDatasetType DatasetType,
    SceneExtent? Extent,
    string Crs,
    int CacheMaxAgeSeconds,
    string? EditionGate,
    bool RequiresAuth,
    string CreatedBy,
    Action<string>? OnOverwroteStaleFinalDir = null,
    Action<Guid, bool>? OnRegistrationCompensated = null,
    Action<Guid, Exception>? OnRegistrationCompensationFailed = null);
