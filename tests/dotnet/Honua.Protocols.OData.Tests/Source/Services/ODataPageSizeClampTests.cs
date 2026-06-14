// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Unit tests for the OData server-driven paging cap (OData:MaxPageSize, #1644).
/// A very large $top on an ad hoc spatial query (geo.intersects + $select) must be
/// clamped so the server never materializes an unbounded page; the streaming handler
/// then emits an @odata.nextLink carrying the clamped $top so clients page through.
/// </summary>
public sealed class ODataPageSizeClampTests
{
    private static ODataValidationService CreateService(int maxPageSize = 1000, int maxRecordCount = 10000)
    {
        var limits = new LimitsOptions
        {
            Query = new QueryLimits { MaxRecordCount = maxRecordCount }
        };
        var validator = new CommonQueryValidator(Options.Create(limits));
        var options = Options.Create(new ODataOptions { MaxPageSize = maxPageSize });
        return new ODataValidationService(validator, options);
    }

    [UnitTest]
    public void ValidateAndNormalizePagination_TopAboveMaxPageSize_ClampsToMaxPageSize()
    {
        var service = CreateService(maxPageSize: 1000);

        var result = service.ValidateAndNormalizePagination(offset: 0, limit: 4000);

        result.IsValid.Should().BeTrue();
        result.Value!.Limit.Should().Be(1000);
        result.Value.Offset.Should().Be(0);
    }

    [UnitTest]
    public void ValidateAndNormalizePagination_TopBelowMaxPageSize_PassesThrough()
    {
        var service = CreateService(maxPageSize: 1000);

        var result = service.ValidateAndNormalizePagination(offset: 0, limit: 50);

        result.IsValid.Should().BeTrue();
        result.Value!.Limit.Should().Be(50);
    }

    [UnitTest]
    public void ValidateAndNormalizePagination_TopEqualsMaxPageSize_PassesThrough()
    {
        var service = CreateService(maxPageSize: 1000);

        var result = service.ValidateAndNormalizePagination(offset: 0, limit: 1000);

        result.IsValid.Should().BeTrue();
        result.Value!.Limit.Should().Be(1000);
    }

    [UnitTest]
    public void ValidateAndNormalizePagination_TopOmitted_DefaultIsClampedToMaxPageSize()
    {
        // QueryLimits.DefaultRecordCount defaults to 1000; a smaller MaxPageSize must
        // still bound the effective default page size.
        var service = CreateService(maxPageSize: 500);

        var result = service.ValidateAndNormalizePagination(offset: 0, limit: null);

        result.IsValid.Should().BeTrue();
        result.Value!.Limit.Should().Be(500);
    }

    [UnitTest]
    public void ValidateAndNormalizePagination_PreservesOffsetWhenClamping()
    {
        var service = CreateService(maxPageSize: 1000);

        var result = service.ValidateAndNormalizePagination(offset: 2000, limit: 8000);

        result.IsValid.Should().BeTrue();
        result.Value!.Offset.Should().Be(2000);
        result.Value.Limit.Should().Be(1000);
    }

    [UnitTest]
    public void ClampPageSize_LargeValue_ReturnsMaxPageSize()
    {
        var service = CreateService(maxPageSize: 1000);

        service.ClampPageSize(10000).Should().Be(1000);
        service.MaxPageSize.Should().Be(1000);
    }

    [UnitTest]
    public void ClampPageSize_NonPositiveValue_ReturnsMaxPageSize()
    {
        var service = CreateService(maxPageSize: 1000);

        service.ClampPageSize(0).Should().Be(1000);
        service.ClampPageSize(-5).Should().Be(1000);
    }

    [UnitTest]
    public void ClampPageSize_SmallValue_ReturnsRequestedSize()
    {
        var service = CreateService(maxPageSize: 1000);

        service.ClampPageSize(250).Should().Be(250);
    }
}
