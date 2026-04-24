// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Tests for the canonical-JSON emitter. Covers three of the ticket 788
/// acceptance criteria: (a) alphabetical keys with preserved array order,
/// (b) grammar + capability version embedded at top, (c) comment sidecar
/// keyed by JSON-Pointer.
/// </summary>
public sealed class SpecCanonicalizerTests
{
    private static SpecDocument Parse(string text)
    {
        var result = new SpecParser().Parse(text);
        result.Document.Should().NotBeNull();
        return result.Document!;
    }

    private static JsonDocument Canonicalize(string text)
    {
        var doc = Parse(text);
        var json = new SpecCanonicalizer().ToJson(doc);
        return JsonDocument.Parse(json);
    }

    [Fact]
    public void Canonicalize_EmitsSchemaAndCapabilitiesAtTop()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            """);

        doc.RootElement.GetProperty("$schema").GetString()
            .Should().Be(SpecGrammarVersion.SchemaUrl);

        var caps = doc.RootElement.GetProperty("capabilities");
        caps.GetProperty("operators").GetString().Should().Be(SpecGrammarVersion.CurrentOperatorCapability);

        doc.RootElement.GetProperty("grammar").GetString().Should().Be("v1.0");
    }

    [Fact]
    public void Canonicalize_RootKeysAreAlphabeticallySorted()
    {
        using var doc = Canonicalize("""
            title   "t"
            kind    "analysis"
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output n { expr = @a }
            """);

        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
        keys.Should().StartWith("$schema");
    }

    [Fact]
    public void Canonicalize_ObjectFieldsAreAlphabeticallySortedWithinObjects()
    {
        // The text declares fields in a non-alphabetical order; the canonical
        // form must sort them inside the source's property bag.
        using var doc = Canonicalize("""
            grammar "v1.0"
            source hospitals {
              ref  = "osm:amenity=hospital"
              type = "layer"
            }
            """);

        var src = doc.RootElement.GetProperty("sources")[0];
        src.EnumerateObject().Select(p => p.Name)
            .Should().ContainInConsecutiveOrder("id", "ref", "type");
    }

    [Fact]
    public void Canonicalize_ComputeEmittedAsArrayPreservingDeclarationOrder()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b1 { op = filter, inputs = { input = @a }, params = { where = "x = 1" } }
            compute a1 { op = filter, inputs = { input = @a }, params = { where = "x = 2" } }
            """);

        var compute = doc.RootElement.GetProperty("compute");
        compute.ValueKind.Should().Be(JsonValueKind.Array);
        compute[0].GetProperty("id").GetString().Should().Be("b1", "declaration order is preserved even though b1 > a1 alphabetically");
        compute[1].GetProperty("id").GetString().Should().Be("a1");
    }

    [Fact]
    public void Canonicalize_SourcesPreserveDeclarationOrder()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source zeta  { type = "layer", ref = "z" }
            source alpha { type = "layer", ref = "a" }
            """);

        var sources = doc.RootElement.GetProperty("sources");
        sources[0].GetProperty("id").GetString().Should().Be("zeta");
        sources[1].GetProperty("id").GetString().Should().Be("alpha");
    }

    [Fact]
    public void Canonicalize_ArrayLiteralPreservesOrder()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            map { layers = ["c", "a", "b"] }
            """);

        var layers = doc.RootElement.GetProperty("map").GetProperty("layers");
        layers.EnumerateArray().Select(e => e.GetString())
            .Should().ContainInConsecutiveOrder("c", "a", "b");
    }

    [Fact]
    public void Canonicalize_CommentsAreStrippedFromBodyButPreservedInMeta()
    {
        using var doc = Canonicalize("""
            # doc header
            grammar "v1.0"
            # describes hospitals
            source hospitals { type = "layer", ref = "x" }
            """);

        // Body is clean — the source itself carries no comment fields.
        var source = doc.RootElement.GetProperty("sources")[0];
        source.EnumerateObject().Select(p => p.Name).Should().NotContain(n =>
            n.Contains("comment", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("description", StringComparison.OrdinalIgnoreCase));

        // Meta.comments holds them keyed by JSON-Pointer path.
        var comments = doc.RootElement.GetProperty("meta").GetProperty("comments");
        comments.ValueKind.Should().Be(JsonValueKind.Object);
        comments.EnumerateObject().Select(p => p.Name)
            .Should().AllSatisfy(name => name.Should().StartWith("/"));
        comments.EnumerateObject().Select(p => p.Value.GetString())
            .Should().Contain(v => v!.Contains("doc header", StringComparison.Ordinal));
        comments.EnumerateObject().Select(p => p.Value.GetString())
            .Should().Contain(v => v!.Contains("describes hospitals", StringComparison.Ordinal));
    }

    [Fact]
    public void Canonicalize_UnitLiteralEncodedAsStructuredObject()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b {
              op = buffer
              inputs = { input = @a }
              params = { distance = 500.m }
            }
            """);

        var distance = doc.RootElement.GetProperty("compute")[0]
            .GetProperty("params").GetProperty("distance");
        distance.ValueKind.Should().Be(JsonValueKind.Object);
        distance.GetProperty("kind").GetString().Should().Be("distance");
        distance.GetProperty("unit").GetString().Should().Be("m");
        distance.GetProperty("value").GetDouble().Should().Be(500);
    }

    [Fact]
    public void Canonicalize_ReferenceEmittedAsString()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output n { expr = @a }
            """);

        doc.RootElement.GetProperty("outputs")[0]
            .GetProperty("expr").GetString().Should().Be("@a");
    }

    [Fact]
    public void Canonicalize_Cql2EmittedAsFilterExpressionObject()
    {
        using var doc = Canonicalize("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope {
              target = @a
              where  = cql2("state = 'CA'")
            }
            """);

        var where = doc.RootElement.GetProperty("scope")[0].GetProperty("where");
        where.ValueKind.Should().Be(JsonValueKind.Object);
        where.GetProperty("cql2").GetString().Should().Be("state = 'CA'");
    }

    [Fact]
    public void Canonicalize_IsDeterministic_SameDocumentProducesIdenticalBytes()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute c1 {
              op = filter
              inputs = { input = @a }
              params = { where = "x = 1" }
            }
            output o { expr = @c1 }
            """;

        var canon = new SpecCanonicalizer();
        var first = canon.ToUtf8(Parse(text));
        var second = canon.ToUtf8(Parse(text));

        first.Should().BeEquivalentTo(second);
    }

    [Fact]
    public void Canonicalize_ToUtf8MatchesToJsonEncoding()
    {
        var doc = Parse("""
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            """);
        var canon = new SpecCanonicalizer();

        var bytes = canon.ToUtf8(doc);
        var text = canon.ToJson(doc);

        System.Text.Encoding.UTF8.GetString(bytes).Should().Be(text);
    }
}
