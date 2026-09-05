// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Shared security-seam matrix: every read shape must reject a masked field when it is
/// used as a predicate, grouping key, aggregate input, or sort key. Provider adapters
/// resolve the mask once and call this same validator before building their SQL.
/// </summary>
public sealed class FeatureQuerySecurityMatrixTests
{
    public static IEnumerable<object[]> MaskedQueryCases()
    {
        yield return ["primary where", new FeatureQuery { Where = "secret = 1" }];
        // OGC CQL2 is translated before it reaches the shared provider seam. These
        // JSONB accessor shapes cover filter and per-field queryable predicates on
        // storage-backed OGC Features resources (#4154).
        yield return ["OGC CQL2 text filter", new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"attributes\" ->> 'secret' LIKE @p0", ["1%"])
        }];
        yield return ["OGC CQL2 JSON numeric filter", new FeatureQuery
        {
            SqlFilter = new SqlFragment(
                "NULLIF(\"attributes\" ->> 'secret', '')::integer >= @p0",
                [100])
        }];
        yield return ["OGC queryable parameter", new FeatureQuery
        {
            SqlFilter = new SqlFragment("\"attributes\" ->> 'secret' = @p0", ["classified"])
        }];
        yield return ["outStatistics", new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Max,
                OnStatisticField = "secret",
                OutStatisticFieldName = "max_secret"
            })
        }];
        yield return ["groupBy", new FeatureQuery
        {
            GroupByFields = ImmutableArray.Create("secret")
        }];
        yield return ["having", new FeatureQuery
        {
            Having = ImmutableArray.Create(new HavingCondition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "secret",
                Operator = HavingComparisonOperator.GreaterThan,
                Value = 0
            })
        }];
        yield return ["orderBy", new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(new OrderByClause("secret"))
        }];
        yield return ["OGC sortby", new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(new OrderByClause("secret", ascending: false))
        }];
        yield return ["storage mapped statistics", new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(new StatisticDefinition
            {
                StatisticType = StatisticType.Min,
                OnStatisticField = "secret",
                OutStatisticFieldName = "min_secret"
            })
        }];
        yield return ["related records", new FeatureQuery { Where = "secret = 1" }];
        yield return ["temporal filter", new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "secret",
                PropertyType = TemporalPropertyType.DateTime
            }
        }];
    }

    [Theory]
    [MemberData(nameof(MaskedQueryCases))]
    public void EveryReadPathRejectsMaskedFieldReferences(string path, FeatureQuery query)
    {
        path.Should().NotBeNullOrWhiteSpace();
        var effectiveQuery = query with
        {
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };

        var act = () => FeatureQuerySecurity.Validate(effectiveQuery);

        act.Should().Throw<ArgumentException>().WithMessage("*secret*");
    }

    [Theory]
    [InlineData("outStatistics")]
    [InlineData("summary")]
    public void H3ReadPathRejectsMaskedFieldReferences(string path)
    {
        var query = new FeatureQuery
        {
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };
        var h3 = path == "outStatistics"
            ? new H3AggregationQuery
            {
                Resolution = 5,
                OutStatistics = ImmutableArray.Create(new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "secret",
                    OutStatisticFieldName = "count_secret"
                })
            }
            : new H3AggregationQuery
            {
                Resolution = 5,
                SummaryDefinitions = ImmutableArray.Create(new SpatialAggregationSummaryDefinition
                {
                    Id = "secret_summary",
                    Kind = SpatialAggregationSummaryKind.Max,
                    Field = "secret"
                })
            };

        var act = () => FeatureQuerySecurity.ValidateH3(query, h3);

        act.Should().Throw<ArgumentException>().WithMessage("*secret*");
    }

    [Theory]
    [InlineData("numeric bins")]
    [InlineData("date bins")]
    public void BinReadPathsRejectMaskedFieldReferences(string path)
    {
        var query = new FeatureQuery { EnforcedMaskedFields = ["secret"] };
        Action act = path == "numeric bins"
            ? () => FeatureQuerySecurity.ValidateBins(query, new BinDefinition
            {
                Type = BinType.Classification,
                Field = "secret"
            })
            : () => FeatureQuerySecurity.ValidateDateBins(query, new DateBinDefinition
            {
                BinField = "secret"
            });

        act.Should().Throw<ArgumentException>().WithMessage("*secret*");
    }

    [Fact]
    public void JsonAccessorMatrixMatchesTheCompleteAttributeKey()
    {
        var masked = new FeatureQuery
        {
            EnforcedMaskedFields = ["secret"],
            Where = "attributes ->> 'secretary' = 'x'"
        };

        var act = () => FeatureQuerySecurity.Validate(masked);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("owner", "alice", "alice", false, true)]
    [InlineData("other owner", "alice", "bob", false, false)]
    [InlineData("legacy row", null, "alice", false, false)]
    [InlineData("admin override", "alice", "bob", true, true)]
    public void ReplicaOwnershipMatrixEnforcesOwnerPolicy(
        string path,
        string? ownerId,
        string? principalId,
        bool isAdmin,
        bool expected)
    {
        path.Should().NotBeNullOrWhiteSpace();

        ReplicaSecurity.CanAccess(ownerId, principalId, isAdmin).Should().Be(expected);
    }

    [Fact]
    public void ReplicaChangeMatrixFiltersCurrentRowsAndFailsClosedForDeletes()
    {
        var changes = new[]
        {
            NewChange(1, FeatureChangeOperation.Insert),
            NewChange(2, FeatureChangeOperation.Update),
            NewChange(3, FeatureChangeOperation.Delete)
        };

        var filtered = ReplicaSecurity.FilterChangeIds(
            changes,
            new HashSet<long> { 1 },
            suppressDeletes: true);

        filtered.InsertIds.Should().Equal(1);
        filtered.UpdateIds.Should().BeEmpty();
        filtered.DeleteIds.Should().BeEmpty();
    }

    private static FeatureChange NewChange(long objectId, FeatureChangeOperation operation)
        => new()
        {
            ChangeId = objectId,
            Generation = objectId,
            LayerId = 1,
            ObjectId = objectId,
            Operation = operation,
            ChangedAt = DateTimeOffset.UtcNow
        };
}
