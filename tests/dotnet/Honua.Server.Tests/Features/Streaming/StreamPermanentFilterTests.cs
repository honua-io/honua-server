// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

[Protocol(TestProtocols.Streaming)]
public sealed class StreamPermanentFilterTests
{
    [UnitTest]
    public void Append_AddsComparablePermanentFilter()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("classification <> 'secret'"),
            Filters(),
            predicates);

        act.Should().NotThrow();
        predicates.Should().ContainSingle();
        var security = new StreamSubscriberSecurity(
            new HashSet<string>(StringComparer.Ordinal) { "svc" },
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>
            {
                [("svc", 1)] = new StreamLayerReadPolicy(predicates, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            },
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>());
        security.Allows(Envelope("public")).Should().BeTrue();
        security.Allows(Envelope("secret")).Should().BeFalse();
    }

    [UnitTest]
    public void Append_RejectsUnsupportedLanguage()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("classification <> 'secret'", "not-a-language"),
            Filters(),
            predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void Append_RejectsAFilterDeeperThanTheStreamingLimit()
    {
        var predicates = new List<FilterExpression>();
        var expression = string.Join(
            " AND ",
            Enumerable.Repeat("classification <> 'a'", InMemoryFilterEvaluator.MaxStreamingDepth + 2));

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression),
            Filters(),
            predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void Append_LeavesPredicatesUntouchedWhenTheLayerHasNoPermanentFilter()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(null),
            Filters(),
            predicates);

        act.Should().NotThrow();
        predicates.Should().BeEmpty();
    }

    private static FilterExpressionService Filters()
        => new FilterExpressionService(new FilterExpressionTranslator(Substitute.For<ISqlFilterTranslator>()));

    private static MetadataV2Object Resource(string? expression, string? language = null)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "roads", Name = "roads" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "classification", Type = MetadataV2FieldType.String }
            ],
            PermanentFilter = expression is null
                ? null
                : new MetadataV2PermanentFilter
                {
                    Expression = expression,
                    Language = language ?? MetadataV2PermanentFilterLanguages.GeoServicesSql
                }
        };

    private static FeatureStreamEnvelope Envelope(string classification)
        => new()
        {
            EventId = "event",
            Cursor = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Protocol = "rest",
            RequestId = "request",
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 10,
            FeatureId = "10",
            Operation = "update",
            Attributes = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["classification"] = System.Text.Json.JsonSerializer.SerializeToElement(classification)
            }
        };
}
