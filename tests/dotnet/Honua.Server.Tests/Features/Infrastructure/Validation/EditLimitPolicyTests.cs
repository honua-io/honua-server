// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

/// <summary>
/// The shared edit-limit policy used by FeatureServer applyEdits and replica upload, OData
/// <c>$batch</c> and gRPC ApplyEdits.
/// </summary>
public sealed class EditLimitPolicyTests
{
    private static readonly EditLimits Limits = new() { MaxFeaturesPerEdit = 2, MaxEditsPerTransaction = 5 };

    [UnitTheory]
    [InlineData(2, 2, 1, null, 0)]
    [InlineData(3, 0, 0, "FeaturesPerOperation", 2)]
    [InlineData(0, 3, 0, "FeaturesPerOperation", 2)]
    [InlineData(0, 0, 3, "FeaturesPerOperation", 2)]
    [InlineData(2, 2, 2, "EditsPerRequest", 5)]
    [InlineData(3, 2, 2, "FeaturesPerOperation", 2)]
    public void Evaluate_AppliesPerOperationLimitBeforeRequestTotal(
        int adds,
        int updates,
        int deletes,
        string? expectedKind,
        int expectedLimit)
    {
        var violation = EditLimitPolicy.Evaluate(new EditCounts(adds, updates, deletes), Limits);

        if (expectedKind is null)
        {
            violation.Should().BeNull();
            return;
        }

        violation.Should().Be(new EditLimitViolation(Enum.Parse<EditLimitViolationKind>(expectedKind), expectedLimit));
    }
}
