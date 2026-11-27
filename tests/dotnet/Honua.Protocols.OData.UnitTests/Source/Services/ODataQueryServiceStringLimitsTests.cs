// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Protocols.OData.UnitTests.Services;

public sealed class ODataQueryServiceStringLimitsTests
{
    private static ODataQueryService CreateService()
    {
        var filterService = new FilterExpressionService(Substitute.For<IFilterExpressionTranslator>());
        return new ODataQueryService(
            filterService,
            Substitute.For<IQueryParameterAdapter<ODataQueryParameters>>(),
            Substitute.For<IQueryProcessor>());
    }

    [UnitTest]
    public void ApplyBasicFilter_RuntimeReplaceExceedingDerivedStringLimit_ThrowsArgumentException()
    {
        var service = CreateService();
        var largeName = new string('a', 10_000);
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = largeName }
        };

        var act = () => service.ApplyBasicFilter(rows, "replace(Name, 'a', 'aaaaaaaaaa') eq 'x'").ToArray();

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{FilterStringAmplificationGuard.MaxDerivedStringLength}*");
    }

    [UnitTest]
    public void ApplyBasicFilter_RuntimeConcatExceedingDerivedStringLimit_ThrowsArgumentException()
    {
        var service = CreateService();
        var largeName = new string('a', 40_000);
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = largeName }
        };

        var act = () => service.ApplyBasicFilter(rows, "concat(Name, Name) eq 'x'").ToArray();

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{FilterStringAmplificationGuard.MaxDerivedStringLength}*");
    }

    [UnitTest]
    public void ApplyBasicFilter_NormalReplaceAndConcat_EvaluatesMatch()
    {
        var service = CreateService();
        var rows = new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Main St" }
        };

        var matched = service.ApplyBasicFilter(rows, "concat(replace(Name, 'St', 'Street'), '!') eq 'Main Street!'").ToArray();

        matched.Should().ContainSingle();
    }
}
