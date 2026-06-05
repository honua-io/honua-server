// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for <see cref="FeatureQueryValidator"/> resultRecordCount clamping (#1463).
/// </summary>
public sealed class FeatureQueryValidatorClampTests
{
    private static FeatureQueryValidator CreateValidator(int maxRecordCount = 2000)
    {
        var limits = new LimitsOptions();
        limits.Query.MaxRecordCount = maxRecordCount;
        var common = new CommonQueryValidator(Options.Create(limits));
        return new FeatureQueryValidator(common);
    }

    // BUG B: resultRecordCount above maxRecordCount must clamp to maxRecordCount, not fail.
    [UnitTest]
    public void ValidateQueryLimits_WithResultRecordCountAboveMax_ClampsToMax()
    {
        var validator = CreateValidator(maxRecordCount: 2000);
        var parameters = new QueryParameters { ResultRecordCount = 99999 };

        var result = validator.ValidateQueryLimits(parameters);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.ValidatedParameters!.ResultRecordCount.Should().Be(2000);
    }

    [UnitTest]
    public void ValidateQueryLimits_WithResultRecordCountWithinMax_IsUnchanged()
    {
        var validator = CreateValidator(maxRecordCount: 2000);
        var parameters = new QueryParameters { ResultRecordCount = 500 };

        var result = validator.ValidateQueryLimits(parameters);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.ValidatedParameters!.ResultRecordCount.Should().Be(500);
    }

    // Zero must still be rejected (MinLimit is 1).
    [UnitTest]
    public void ValidateQueryLimits_WithZeroResultRecordCount_Fails()
    {
        var validator = CreateValidator();
        var parameters = new QueryParameters { ResultRecordCount = 0 };

        var result = validator.ValidateQueryLimits(parameters);

        result.IsValid.Should().BeFalse();
    }
}
