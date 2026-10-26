// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Number of features a single edit request touches, per edit kind.
/// </summary>
/// <param name="Adds">Features created.</param>
/// <param name="Updates">Features updated.</param>
/// <param name="Deletes">Features deleted.</param>
internal readonly record struct EditCounts(int Adds, int Updates, int Deletes)
{
    /// <summary>Total number of edits across every kind.</summary>
    public int Total => Adds + Updates + Deletes;
}

/// <summary>
/// Which configured edit limit a request exceeded.
/// </summary>
internal enum EditLimitViolationKind
{
    /// <summary>One edit kind exceeds <see cref="EditLimits.MaxFeaturesPerEdit"/>.</summary>
    FeaturesPerOperation,

    /// <summary>The request as a whole exceeds <see cref="EditLimits.MaxEditsPerTransaction"/>.</summary>
    EditsPerRequest
}

/// <summary>
/// A configured edit limit the request exceeded.
/// </summary>
/// <param name="Kind">The limit that was exceeded.</param>
/// <param name="Limit">The configured maximum for that limit.</param>
internal readonly record struct EditLimitViolation(EditLimitViolationKind Kind, int Limit);

/// <summary>
/// The one edit-limit policy shared by every edit surface (FeatureServer applyEdits and replica
/// upload, OData <c>$batch</c>, gRPC ApplyEdits). <see cref="EditLimits.MaxFeaturesPerEdit"/>
/// applies to each edit kind separately; <see cref="EditLimits.MaxEditsPerTransaction"/> applies
/// to the request total. Protocol adapters map a violation onto their own error shape.
/// </summary>
internal static class EditLimitPolicy
{
    /// <summary>
    /// Evaluates edit counts against the configured limits. The per-operation limit is checked
    /// before the per-request limit.
    /// </summary>
    /// <param name="counts">Edit counts for the request.</param>
    /// <param name="limits">Configured edit limits.</param>
    /// <returns>The first violated limit, or null when the request is within limits.</returns>
    internal static EditLimitViolation? Evaluate(EditCounts counts, EditLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (counts.Adds > limits.MaxFeaturesPerEdit ||
            counts.Updates > limits.MaxFeaturesPerEdit ||
            counts.Deletes > limits.MaxFeaturesPerEdit)
        {
            return new EditLimitViolation(EditLimitViolationKind.FeaturesPerOperation, limits.MaxFeaturesPerEdit);
        }

        return counts.Total > limits.MaxEditsPerTransaction
            ? new EditLimitViolation(EditLimitViolationKind.EditsPerRequest, limits.MaxEditsPerTransaction)
            : null;
    }
}
