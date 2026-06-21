// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Centralised validation for network-dataset editing (#1882). The dataset id and the
/// edge/vertex table names are interpolated into the pgRouting edges-SQL string (which
/// cannot bind those positions), so they must be constrained to safe identifiers
/// <em>before</em> they are persisted or resolved. Keeping the rules here means the
/// read path (<c>NetworkDatasetRegistry</c>) and the admin editing surface share one
/// definition of "safe".
/// </summary>
public static partial class NetworkDatasetValidation
{
    /// <summary>The set of accepted lifecycle statuses.</summary>
    public static readonly IReadOnlySet<string> AllowedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "active", "building", "disabled" };

    private const int MaxIdentifierLength = 256;
    private const int MaxNameLength = 256;
    private const int MaxDescriptionLength = 2048;

    // Lowercase slug for the dataset id: starts with a letter, then letters/digits/_/-.
    [GeneratedRegex(@"^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DatasetIdPattern();

    // Schema-qualified or bare lowercase SQL identifier: optional "schema." prefix, each
    // part starting with a letter/underscore and limited to [a-z0-9_]. Conservative by
    // design because the value is interpolated into SQL text, not bound.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*(\.[a-z_][a-z0-9_]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifierPattern();

    /// <summary>
    /// Validates a network-dataset id.
    /// </summary>
    /// <returns><c>true</c> when valid; otherwise <c>false</c> with <paramref name="error"/> set.</returns>
    public static bool TryValidateId(string? id, out string error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Network dataset id is required.";
            return false;
        }

        if (id.Length > MaxIdentifierLength)
        {
            error = $"Network dataset id must be {MaxIdentifierLength} characters or fewer.";
            return false;
        }

        if (!DatasetIdPattern().IsMatch(id))
        {
            error = "Network dataset id must be a lowercase slug (start with a letter; letters, digits, '_' or '-').";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a network-dataset display name.
    /// </summary>
    public static bool TryValidateName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Network dataset name is required.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Network dataset name must be {MaxNameLength} characters or fewer.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a schema-qualified table identifier that will be interpolated into the
    /// pgRouting edges-SQL string.
    /// </summary>
    public static bool TryValidateTableIdentifier(string? identifier, string role, out string error)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            error = $"{role} is required.";
            return false;
        }

        if (identifier.Length > MaxIdentifierLength)
        {
            error = $"{role} must be {MaxIdentifierLength} characters or fewer.";
            return false;
        }

        if (!SqlIdentifierPattern().IsMatch(identifier))
        {
            error = $"{role} '{identifier}' is not a valid schema-qualified identifier (lowercase, optional 'schema.' prefix).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns whether a table identifier is a safe schema-qualified identifier. Used by
    /// the read path to reject a registry row before interpolating its table names.
    /// </summary>
    public static bool IsValidTableIdentifier(string? identifier)
        => !string.IsNullOrWhiteSpace(identifier)
            && identifier.Length <= MaxIdentifierLength
            && SqlIdentifierPattern().IsMatch(identifier);

    /// <summary>
    /// Validates an SRID.
    /// </summary>
    public static bool TryValidateSrid(int srid, out string error)
    {
        // PostGIS spatial_ref_sys SRIDs are positive; 0 is the "unknown" sentinel that
        // routing geometry should not use. Bound the upper end loosely to catch typos.
        if (srid is <= 0 or > 998999)
        {
            error = "Network dataset srid must be a positive PostGIS SRID.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a lifecycle status.
    /// </summary>
    public static bool TryValidateStatus(string? status, out string error)
    {
        if (string.IsNullOrWhiteSpace(status) || !AllowedStatuses.Contains(status))
        {
            error = $"Network dataset status must be one of: {string.Join(", ", AllowedStatuses)}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates an optional description.
    /// </summary>
    public static bool TryValidateDescription(string? description, out string error)
    {
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            error = $"Network dataset description must be {MaxDescriptionLength} characters or fewer.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
