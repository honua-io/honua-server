// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Unit tests for <see cref="GroundingOptionsValidator"/>. The feature slice
/// registers this validator alongside <c>.ValidateOnStart()</c>, so invalid
/// configuration must fail fast instead of silently corrupting banding,
/// capping, or ambiguity evaluation at request time.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class GroundingOptionsValidatorTests
{
    private readonly GroundingOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new GroundingOptions());

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_NonPositiveMaxCandidatesPerKind_ReturnsFailure()
    {
        var options = new GroundingOptions { MaxCandidatesPerKind = 0 };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        (result.Failures ?? []).Should().Contain(f => f.Contains(nameof(GroundingOptions.MaxCandidatesPerKind)));
    }

    [UnitTest]
    public void Validate_NegativeMaxCandidatesPerKind_ReturnsFailure()
    {
        var options = new GroundingOptions { MaxCandidatesPerKind = -1 };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        (result.Failures ?? []).Should().Contain(f => f.Contains(nameof(GroundingOptions.MaxCandidatesPerKind)));
    }

    [UnitTest]
    public void Validate_HighConfidenceFloorBelowMedium_ReturnsFailure()
    {
        var options = new GroundingOptions
        {
            HighConfidenceFloor = 0.30,
            MediumConfidenceFloor = 0.60
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        (result.Failures ?? []).Should().Contain(f =>
            f.Contains(nameof(GroundingOptions.MediumConfidenceFloor))
            && f.Contains(nameof(GroundingOptions.HighConfidenceFloor)));
    }

    [UnitTest]
    public void Validate_FloorsOutsideZeroToOne_ReturnsFailure()
    {
        var options = new GroundingOptions
        {
            WorkflowFamilyFloor = -0.1,
            HighConfidenceFloor = 1.5,
            MediumConfidenceFloor = -0.2,
            MaterialSpread = 1.2
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        var failures = result.Failures ?? [];
        failures.Should().Contain(f => f.Contains(nameof(GroundingOptions.WorkflowFamilyFloor)));
        failures.Should().Contain(f => f.Contains(nameof(GroundingOptions.HighConfidenceFloor)));
        failures.Should().Contain(f => f.Contains(nameof(GroundingOptions.MediumConfidenceFloor)));
        failures.Should().Contain(f => f.Contains(nameof(GroundingOptions.MaterialSpread)));
    }
}
