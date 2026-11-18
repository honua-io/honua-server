// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Tests.Features.FeatureStore;

// Protocol boundaries map a hidden-field rejection to a caller error by its type, so every
// rejection path must raise the typed exception rather than a bare ArgumentException.
public sealed class MaskedFieldRejectionTypeTests
{
    private static readonly ImmutableArray<string> Masked = ImmutableArray.Create("secret");

    public static TheoryData<string, FeatureQuery> RejectedQueries() => new()
    {
        { "where", new FeatureQuery { Where = "secret = 'a'", EnforcedMaskedFields = Masked } },
        { "parsed filter", new FeatureQuery { SqlFilter = new SqlFragment("secret = @p0", ["a"]), EnforcedMaskedFields = Masked } },
        { "order by", new FeatureQuery { OrderBy = ImmutableArray.Create(OrderByClause.Asc("secret")), EnforcedMaskedFields = Masked } },
        { "group by", new FeatureQuery { GroupByFields = ImmutableArray.Create("secret"), EnforcedMaskedFields = Masked } },
    };

    [Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(RejectedQueries))]
    public void RejectionIsTheTypedCallerError(string surface, FeatureQuery query)
    {
        surface.Should().NotBeNullOrWhiteSpace();

        var act = () => FeatureQuerySecurity.Validate(query);

        act.Should().ThrowExactly<MaskedFieldReferenceException>().WithMessage("*secret*masked*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ReadableFieldIsNotRejected()
    {
        var query = new FeatureQuery { Where = "name = 'a'", EnforcedMaskedFields = Masked };

        var act = () => FeatureQuerySecurity.Validate(query);

        act.Should().NotThrow();
    }
}
