// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene;

public static partial class SceneDatasetValidator
{
    public const int MaxSceneIdLength = 64;

    public const int MaxSceneNameLength = 128;

    public const int MaxTilesetFileNameLength = 64;

    public const int MaxCrsLength = 32;

    public const int MaxCacheMaxAgeSeconds = 86_400;

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SceneIdPattern();

    [GeneratedRegex(@"^[A-Z]+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CrsPattern();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex EditionGatePattern();

    public static bool TryValidateSceneId(string? id, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Scene id is required.";
            return false;
        }

        if (id.Length > MaxSceneIdLength)
        {
            error = $"Scene id must be {MaxSceneIdLength} characters or fewer.";
            return false;
        }

        if (!SceneIdPattern().IsMatch(id))
        {
            error = "Scene id must contain only lowercase letters, digits, and hyphens, and may not start or end with a hyphen.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateName(string? name, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Scene name is required.";
            return false;
        }

        if (name.Length > MaxSceneNameLength)
        {
            error = $"Scene name must be {MaxSceneNameLength} characters or fewer.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateAssetRoot(string? assetRoot, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(assetRoot))
        {
            error = "Asset root is required.";
            return false;
        }

        // Reject URL-shaped roots (http/https/file/etc). The hosted serving
        // resolver only knows how to read from the local filesystem.
        if (assetRoot.Contains("://", StringComparison.Ordinal))
        {
            error = "Asset root must be a local filesystem path; URI schemes are not supported.";
            return false;
        }

        if (assetRoot.Contains("..", StringComparison.Ordinal))
        {
            error = "Asset root must not contain parent-directory traversal segments.";
            return false;
        }

        if (assetRoot.Contains('\\', StringComparison.Ordinal))
        {
            error = "Asset root must use forward slashes; backslashes are not permitted.";
            return false;
        }

        if (ContainsShellMetacharacter(assetRoot))
        {
            error = "Asset root must not contain shell metacharacters.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateTilesetFileName(string? tilesetFileName, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(tilesetFileName))
        {
            error = null;
            return true;
        }

        if (tilesetFileName.Length > MaxTilesetFileNameLength)
        {
            error = $"Tileset filename must be {MaxTilesetFileNameLength} characters or fewer.";
            return false;
        }

        if (tilesetFileName.Contains('/', StringComparison.Ordinal)
            || tilesetFileName.Contains('\\', StringComparison.Ordinal))
        {
            error = "Tileset filename must not contain path separators.";
            return false;
        }

        if (tilesetFileName.Contains("..", StringComparison.Ordinal))
        {
            error = "Tileset filename must not contain parent-directory traversal segments.";
            return false;
        }

        if (ContainsShellMetacharacter(tilesetFileName))
        {
            error = "Tileset filename must not contain shell metacharacters.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateCrs(string? crs, [NotNullWhen(false)] out string? error)
    {
        if (crs is null)
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(crs))
        {
            error = "CRS must be null or a non-empty authority token.";
            return false;
        }

        if (crs.Length > MaxCrsLength)
        {
            error = $"CRS must be {MaxCrsLength} characters or fewer.";
            return false;
        }

        if (!CrsPattern().IsMatch(crs))
        {
            error = "CRS must be an authority token of the form 'AUTHORITY:CODE' (e.g. 'EPSG:4979').";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateCachePolicy(SceneCachePolicy policy, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.MaxAgeSeconds < 0 || policy.MaxAgeSeconds > MaxCacheMaxAgeSeconds)
        {
            error = $"Cache max-age must be between 0 and {MaxCacheMaxAgeSeconds} seconds.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateEditionGate(string? editionGate, [NotNullWhen(false)] out string? error)
    {
        if (editionGate is null)
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(editionGate) || !EditionGatePattern().IsMatch(editionGate))
        {
            error = "Edition gate must be a lowercase slug (letters, digits, hyphens).";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateExtent(SceneExtent? extent, [NotNullWhen(false)] out string? error)
    {
        if (extent is null)
        {
            error = null;
            return true;
        }

        if (!IsFiniteLongitude(extent.XMin) || !IsFiniteLongitude(extent.XMax))
        {
            error = "Extent longitude bounds must be between -180 and 180.";
            return false;
        }

        if (!IsFiniteLatitude(extent.YMin) || !IsFiniteLatitude(extent.YMax))
        {
            error = "Extent latitude bounds must be between -90 and 90.";
            return false;
        }

        if (extent.XMin > extent.XMax || extent.YMin > extent.YMax)
        {
            error = "Extent min bounds must be less than or equal to max bounds.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateAccessFlags(bool isPublic, bool requiresAuth, [NotNullWhen(false)] out string? error)
    {
        if (isPublic && requiresAuth)
        {
            error = "A scene cannot be both public and require authentication.";
            return false;
        }

        if (!isPublic && !requiresAuth)
        {
            error = "A scene must declare exactly one of isPublic=true or requiresAuth=true.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsFiniteLongitude(double value)
        => double.IsFinite(value) && value >= -180.0 && value <= 180.0;

    private static bool IsFiniteLatitude(double value)
        => double.IsFinite(value) && value >= -90.0 && value <= 90.0;

    private static bool ContainsShellMetacharacter(string value)
    {
        // These characters never appear in well-formed filesystem path inputs
        // and indicate either operator error or an injection attempt.
        ReadOnlySpan<char> forbidden = ['\0', ';', '|', '&', '$', '<', '>', '`', '\n', '\r', '*', '?', '"', '\''];
        return value.AsSpan().IndexOfAny(forbidden) >= 0;
    }
}
