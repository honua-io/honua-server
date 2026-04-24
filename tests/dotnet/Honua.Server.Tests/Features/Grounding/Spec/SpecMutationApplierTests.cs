// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.Server.Features.Grounding.Spec;

namespace Honua.Server.Tests.Features.Grounding.Spec;

/// <summary>
/// Regression coverage for grounding mutations that materialize typed spec
/// literals from planner-emitted strings.
/// </summary>
public sealed class SpecMutationApplierTests
{
    [Theory]
    [InlineData("250.ms", SpecTypeKind.Duration, "ms", 250d)]
    [InlineData("2.km2", SpecTypeKind.Area, "km2", 2d)]
    public void Apply_AddComputeMutation_PreservesGrammarUnitSemantics(
        string literalText,
        SpecTypeKind expectedKind,
        string expectedUnit,
        double expectedValue)
    {
        var applier = new SpecMutationApplier();
        var document = applier.Apply(
            CreateEmptySpecDocument(),
            new SpecMutation[]
            {
                new AddComputeMutation(
                    "derived",
                    "buffer",
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["measure"] = literalText
                    })
            });

        var literal = document.Compute.Single().Parameters!.Fields.Single(field => field.Key == "measure").Value
            .Should().BeOfType<LiteralNode>().Subject;

        literal.Kind.Should().Be(expectedKind);
        literal.Unit.Should().Be(expectedUnit);
        literal.Number.Should().Be(expectedValue);
    }

    private static SpecDocument CreateEmptySpecDocument()
        => new(
            SourceSpan.Synthetic,
            SpecGrammarVersion.Current,
            SourceSpan.Synthetic,
            "analysis",
            null,
            ImmutableArray<SourceBinding>.Empty,
            ImmutableArray<ScopeClause>.Empty,
            ImmutableArray<ComputeStep>.Empty,
            null,
            ImmutableArray<OutputBinding>.Empty,
            ImmutableDictionary<string, string>.Empty);
}
