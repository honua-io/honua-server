// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Round-trip invariants from ticket 788: text → JSON → AST → JSON is
/// idempotent, and semantically equivalent to the original source.
/// </summary>
public sealed class SpecRoundTripTests
{
    private static readonly SpecParser _parser = new();
    private static readonly SpecCanonicalizer _canon = new();

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Roundtrip_JsonIsIdempotentAcrossParseAndEmit(string _, string source)
    {
        // text → AST → JSON
        var first = _canon.ToJson(_parser.Parse(source).Document!);

        // JSON → AST → JSON (should byte-match)
        var reparsedDoc = SpecJsonReader.Read(first);
        var second = _canon.ToJson(reparsedDoc);

        second.Should().Be(first);
    }

    [Fact]
    public void Roundtrip_PreservesSourceIdsInDeclarationOrder()
    {
        const string source = """
            grammar "v1.0"
            source zeta  { type = "layer", ref = "z" }
            source alpha { type = "layer", ref = "a" }
            source beta  { type = "layer", ref = "b" }
            """;

        var doc = _parser.Parse(source).Document!;
        var json = _canon.ToJson(doc);
        var reparsed = SpecJsonReader.Read(json);

        reparsed.Sources.Select(s => s.Id).Should().ContainInConsecutiveOrder("zeta", "alpha", "beta");
    }

    [Fact]
    public void Roundtrip_PreservesUnitLiteralsInComputeParams()
    {
        const string source = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b {
              op = buffer
              inputs = { input = @a }
              params = { distance = 500.m, crs = "EPSG:3857" }
            }
            """;

        var doc = _parser.Parse(source).Document!;
        var json = _canon.ToJson(doc);
        var reparsed = SpecJsonReader.Read(json);

        var distance = reparsed.Compute[0].Parameters!.Fields.Single(f => f.Key == "distance");
        var literal = distance.Value.Should().BeOfType<LiteralNode>().Subject;
        literal.Kind.Should().Be(SpecTypeKind.Distance);
        literal.Unit.Should().Be("m");
        literal.Number.Should().Be(500);
    }

    [Fact]
    public void Roundtrip_PreservesReferenceWithCallSuffix()
    {
        const string source = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output n { expr = @compute.foo.count() }
            """;

        var doc = _parser.Parse(source).Document!;
        var json = _canon.ToJson(doc);
        var reparsed = SpecJsonReader.Read(json);

        var reference = reparsed.Outputs[0].Expression.Should().BeOfType<ReferenceNode>().Subject;
        reference.Root.Should().Be("compute");
        reference.Segments.Should().ContainInOrder("foo");
        reference.Call.Should().Be("count");
    }

    [Fact]
    public void Roundtrip_PreservesCql2FilterAndScope()
    {
        const string source = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope {
              target = @a
              where  = cql2("state = 'CA' AND pop > 1000")
            }
            """;

        var doc = _parser.Parse(source).Document!;
        var json = _canon.ToJson(doc);
        var reparsed = SpecJsonReader.Read(json);

        var scope = reparsed.Scopes.Single();
        scope.Target.Canonical.Should().Be("@a");
        var cql = scope.Where.Should().BeOfType<Cql2Expression>().Subject;
        cql.Cql2Text.Should().Be("state = 'CA' AND pop > 1000");
    }

    [Fact]
    public void Roundtrip_PreservesCommentSidecar()
    {
        const string source = """
            # top-level header
            grammar "v1.0"
            # notes about source
            source hospitals { type = "layer", ref = "x" }
            """;

        var doc = _parser.Parse(source).Document!;
        var json = _canon.ToJson(doc);
        var reparsed = SpecJsonReader.Read(json);

        reparsed.Comments.Should().NotBeEmpty();
        reparsed.Comments.Values.Should().Contain(v => v.Contains("top-level header"));
        reparsed.Comments.Values.Should().Contain(v => v.Contains("notes about source"));
    }

    public static IEnumerable<object[]> RoundTripCases()
    {
        yield return new object[]
        {
            "minimal",
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            """
        };

        yield return new object[]
        {
            "with-compute-and-output",
            """
            grammar "v1.0"
            kind    "analysis"
            title   "demo"
            source a { type = "layer", ref = "x" }
            source b { type = "layer", ref = "y" }
            compute c {
              op = spatial_join
              inputs = { left = @a, right = @b }
              params = { distance = 100.m, crs = "EPSG:3857" }
            }
            output o { expr = @c }
            """
        };

        yield return new object[]
        {
            "with-scope-and-cql2",
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope {
              target = @a
              where  = cql2("state = 'CA'")
            }
            output n { expr = @a }
            """
        };

        yield return new object[]
        {
            "with-map",
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            map {
              layers = ["a", "b"]
              viewport = { center = [-122.0, 37.0], zoom = 10 }
            }
            """
        };
    }
}
