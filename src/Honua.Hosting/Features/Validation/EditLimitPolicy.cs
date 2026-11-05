// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Infrastructure.Validation;

internal readonly record struct EditCounts(int Adds, int Updates, int Deletes)
{
    public int Total => Adds + Updates + Deletes;
}

internal enum EditLimitViolationKind
{
    FeaturesPerOperation,

    EditsPerRequest
}

internal readonly record struct EditLimitViolation(EditLimitViolationKind Kind, int Limit);

internal static class EditLimitPolicy
{
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
