// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Input validation for managed enrichment-dataset registration/update (#2280).
/// Keeps admin-supplied values constrained before they reach the registry store.
/// </summary>
internal static partial class EnrichmentDatasetValidation
{
    private const int MaxIdLength = 100;
    private const int MaxTitleLength = 200;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Validates the dataset id (lowercase slug).</summary>
    /// <param name="id">Candidate id.</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidateId(string? id, out string error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "id is required.";
            return false;
        }

        if (id.Length > MaxIdLength)
        {
            error = $"id must be {MaxIdLength} characters or fewer.";
            return false;
        }

        if (!IdPattern().IsMatch(id))
        {
            error = "id must be a lowercase slug (letters, digits, hyphens) starting with a letter or digit.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Validates the dataset title.</summary>
    /// <param name="title">Candidate title.</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidateTitle(string? title, out string error)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            error = "title is required.";
            return false;
        }

        if (title.Length > MaxTitleLength)
        {
            error = $"title must be {MaxTitleLength} characters or fewer.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Validates and normalizes the category.</summary>
    /// <param name="category">Candidate category.</param>
    /// <param name="normalized">Normalized lowercase category when valid.</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidateCategory(string? category, out string normalized, out string error)
    {
        normalized = EnrichmentDatasetCategories.Boundary;
        if (string.IsNullOrWhiteSpace(category))
        {
            error = string.Empty;
            return true;
        }

        if (!EnrichmentDatasetCategories.IsValid(category))
        {
            error = "category must be one of: boundary, demographic, poi.";
            return false;
        }

        normalized = category.Trim().ToLowerInvariant();
        error = string.Empty;
        return true;
    }

    /// <summary>Validates and normalizes the default spatial predicate.</summary>
    /// <param name="predicate">Candidate predicate.</param>
    /// <param name="normalized">Normalized lowercase predicate when valid.</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidatePredicate(string? predicate, out string normalized, out string error)
    {
        normalized = "intersects";
        if (string.IsNullOrWhiteSpace(predicate))
        {
            error = string.Empty;
            return true;
        }

        normalized = predicate.Trim().ToLowerInvariant();
        if (normalized is not ("intersects" or "contains" or "within" or "dwithin"))
        {
            error = "defaultPredicate must be one of: intersects, contains, within, dwithin.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Validates the backing layer id.</summary>
    /// <param name="layerId">Candidate layer id.</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidateLayerId(int? layerId, out string error)
    {
        if (layerId is not { } id || id < 0)
        {
            error = "layerId must be a non-negative layer identifier.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Validates and parses the minimum edition tier.</summary>
    /// <param name="minimumEdition">Candidate edition name.</param>
    /// <param name="edition">Parsed edition when valid (defaults to Pro when omitted).</param>
    /// <param name="error">Validation error message when invalid.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool TryValidateMinimumEdition(string? minimumEdition, out HonuaEdition edition, out string error)
    {
        edition = HonuaEdition.Pro;
        if (string.IsNullOrWhiteSpace(minimumEdition))
        {
            error = string.Empty;
            return true;
        }

        if (!Enum.TryParse(minimumEdition, ignoreCase: true, out edition))
        {
            error = "minimumEdition must be one of: Community, Pro, Enterprise.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
