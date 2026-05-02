// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene;

/// <summary>
/// Pure shape/syntax validators for scene dataset registration inputs.
/// </summary>
/// <remarks>
/// Validators are intentionally I/O-free so they can run inside admin
/// endpoint handlers without taking on filesystem, network, or database
/// dependencies. Containment of the asset root against a serving directory
/// is enforced separately by the hosted-serving path's
/// <c>SceneAssetResolver</c>.
/// </remarks>
public static partial class SceneDatasetValidator
{
    /// <summary>
    /// Maximum allowed length for a scene id slug.
    /// </summary>
    public const int MaxSceneIdLength = 64;

    /// <summary>
    /// Maximum allowed length for a scene display name.
    /// </summary>
    public const int MaxSceneNameLength = 128;

    /// <summary>
    /// Maximum allowed cache <c>max-age</c>, in seconds (24 hours).
    /// </summary>
    public const int MaxCacheMaxAgeSeconds = 86_400;

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SceneIdPattern();

    [GeneratedRegex(@"^[A-Z]+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CrsPattern();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex EditionGatePattern();

    /// <summary>
    /// Validates the scene id slug format.
    /// </summary>
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

    /// <summary>
    /// Validates the scene display name length and content.
    /// </summary>
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

    /// <summary>
    /// Validates the asset root path shape. Rejects URI schemes, relative
    /// traversal, backslashes, and shell metacharacters that have no place
    /// in a server-side filesystem path.
    /// </summary>
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

    /// <summary>
    /// Validates the optional CRS authority token format.
    /// </summary>
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

        if (!CrsPattern().IsMatch(crs))
        {
            error = "CRS must be an authority token of the form 'AUTHORITY:CODE' (e.g. 'EPSG:4979').";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates the cache policy bounds.
    /// </summary>
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

    /// <summary>
    /// Validates the optional edition gate slug format.
    /// </summary>
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

    /// <summary>
    /// Validates the WGS-84 extent bounds and ordering.
    /// </summary>
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

    /// <summary>
    /// Cross-field check: a record cannot simultaneously declare itself public
    /// and require authentication.
    /// </summary>
    public static bool TryValidateAccessFlags(bool isPublic, bool requiresAuth, [NotNullWhen(false)] out string? error)
    {
        if (isPublic && requiresAuth)
        {
            error = "A scene cannot be both public and require authentication.";
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
        foreach (var c in value)
        {
            if (forbidden.IndexOf(c) >= 0)
            {
                return true;
            }
        }
        return false;
    }
}
