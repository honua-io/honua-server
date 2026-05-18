// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Projections;

/// <summary>
/// Stable semantic role identifier for a source field.
/// </summary>
public readonly record struct FieldSemanticRole
{
    public FieldSemanticRole(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Field semantic role cannot be blank.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public bool IsKnown => FieldSemanticRoleVocabulary.IsKnown(Value);

    public override string ToString() => Value;

    public static FieldSemanticRole Parse(string value)
    {
        if (!TryParse(value, out var role))
        {
            throw new ArgumentException("Field semantic role cannot be blank.", nameof(value));
        }

        return role;
    }

    public static bool TryParse(string? value, out FieldSemanticRole role)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            role = default;
            return false;
        }

        role = new FieldSemanticRole(value);
        return true;
    }

    public static implicit operator FieldSemanticRole(string value) => Parse(value);
}

/// <summary>
/// Well-known metadata v2 field semantic roles.
/// </summary>
public static class FieldSemanticRoleVocabulary
{
    public const string IdentifierPrimary = "identifier.primary";
    public const string DisplayTitle = "display.title";
    public const string GeometryPrimary = "geometry.primary";
    public const string TemporalInstant = "temporal.instant";
    public const string TemporalStart = "temporal.start";
    public const string TemporalEnd = "temporal.end";
    public const string MetadataCreated = "metadata.created";
    public const string MetadataModified = "metadata.modified";
    public const string AssetHref = "asset.href";
    public const string LicenseCode = "license.code";
    public const string QualityFlag = "quality.flag";
    public const string StatusLifecycle = "status.lifecycle";

    private static readonly HashSet<string> KnownRoles = new(StringComparer.Ordinal)
    {
        IdentifierPrimary,
        DisplayTitle,
        GeometryPrimary,
        TemporalInstant,
        TemporalStart,
        TemporalEnd,
        MetadataCreated,
        MetadataModified,
        AssetHref,
        LicenseCode,
        QualityFlag,
        StatusLifecycle
    };

    public static IReadOnlyCollection<string> All => KnownRoles;

    public static bool IsKnown(string? value) => value is not null && KnownRoles.Contains(value);
}

/// <summary>
/// Semantic role assignment for a source field.
/// </summary>
public sealed record FieldSemanticBinding(
    string FieldName,
    FieldSemanticRole Role,
    FieldStandardBinding? StandardBinding = null);

/// <summary>
/// Optional mapping from a field semantic role to an external metadata standard term.
/// </summary>
public sealed record FieldStandardBinding(
    string Standard,
    string? Version = null,
    Uri? Uri = null,
    string? Term = null);
