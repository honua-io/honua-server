// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Unit tests for <see cref="SpecParser"/> — verifies the S1 happy path,
/// diagnostic-collecting error recovery, and that every diagnostic carries
/// a 1-based line+column (the key acceptance criterion from ticket 788).
/// </summary>
public sealed class SpecParserTests
{
    private static SpecParseResult Parse(string text) => new SpecParser().Parse(text);

    [Fact]
    public void Parse_MinimalHappyPath_ReturnsCompleteDocument()
    {
        const string text = """
            grammar "v1.0"
            kind    "analysis"
            title   "demo"

            source hospitals {
              type = "layer"
              ref  = "osm:amenity=hospital"
            }

            output h {
              expr = @hospitals
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        result.IsSuccess.Should().BeTrue();
        result.Document.Should().NotBeNull();
        result.Document!.Grammar.Should().Be("v1.0");
        result.Document.Kind.Should().Be("analysis");
        result.Document.Title.Should().Be("demo");
        result.Document.Sources.Should().HaveCount(1);
        result.Document.Sources[0].Id.Should().Be("hospitals");
        result.Document.Outputs.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_AcceptsTrailingCommasInObjectAndArray()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x", }
            map {
              layers = ["a", "b", ]
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        result.Document!.Sources.Should().HaveCount(1);
        result.Document.Map.Should().NotBeNull();
    }

    [Fact]
    public void Parse_UnitLiteralBecomesDistanceLiteralNode()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b {
              op = buffer
              inputs = { input = @a }
              params = { distance = 500.m }
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        var step = result.Document!.Compute[0];
        var distance = step.Parameters!.Fields.Single(f => f.Key == "distance");
        var literal = distance.Value.Should().BeOfType<LiteralNode>().Subject;
        literal.Kind.Should().Be(SpecTypeKind.Distance);
        literal.Unit.Should().Be("m");
        literal.Number.Should().Be(500);
    }

    [Fact]
    public void Parse_UnknownUnitSuffix_ReportsSyntaxError()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b { op = buffer, inputs = { input = @a }, params = { distance = 5.fathom } }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.SyntaxError &&
            d.Message.Contains("unit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_ReferenceWithCallSuffix_Preserves()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output n {
              expr = @compute.nearby.count()
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        var expr = result.Document!.Outputs[0].Expression;
        var reference = expr.Should().BeOfType<ReferenceNode>().Subject;
        reference.Root.Should().Be("compute");
        reference.Segments.Should().ContainInOrder("nearby");
        reference.Call.Should().Be("count");
        reference.Canonical.Should().Be("@compute.nearby.count()");
    }

    [Fact]
    public void Parse_Cql2CallCapturesEmbeddedExpression()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope {
              target = @a
              where  = cql2("state = 'CA' AND pop > 10000")
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        var where = result.Document!.Scopes.Single().Where;
        var cql = where.Should().BeOfType<Cql2Expression>().Subject;
        cql.Cql2Text.Should().Be("state = 'CA' AND pop > 10000");
    }

    [Fact]
    public void Parse_GeometryLiteralRoundTripsThroughNtsWkt()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope {
              target = @a
              where  = POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        var geom = result.Document!.Scopes.Single().Where.Should().BeOfType<GeometryLiteral>().Subject;
        geom.Geometry.GeometryType.Should().Be("Polygon");
        geom.WellKnownText.Should().Contain("POLYGON");
    }

    // ──────────────────────────── Diagnostics surface line AND column ────────────────────────────

    [Fact]
    public void Parse_MissingEqualsSign_EmitsDiagnosticWithLineAndColumn()
    {
        const string text = """
            grammar "v1.0"
            source a {
              type "layer"
            }
            """;

        var result = Parse(text);

        var diag = result.Diagnostics
            .Should().Contain(d => d.Severity == SpecDiagnosticSeverity.Error)
            .Subject;
        diag.Span.Line.Should().BeGreaterThan(0, "diagnostics must carry a 1-based line number");
        diag.Span.Column.Should().BeGreaterThan(0, "diagnostics must carry a 1-based column number");
    }

    [Fact]
    public void Parse_GarbageAtTopLevel_RecoversAtNextSectionKeyword()
    {
        // Garbage between source blocks — parser should still recover 'b'.
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            !!!bogus
            source b { type = "layer", ref = "y" }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().Contain(d => d.Severity == SpecDiagnosticSeverity.Error);
        result.Document!.Sources.Should().HaveCount(2);
        result.Document.Sources.Select(s => s.Id).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Parse_UnclosedObject_EmitsMissingBraceDiagnostic()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer"
            """;

        var result = Parse(text);

        result.Diagnostics.Should().Contain(d =>
            d.Severity == SpecDiagnosticSeverity.Error &&
            d.Message.Contains('}'));
    }

    [Fact]
    public void Parse_ComputeMissingOp_EmitsMissingRequiredParameter()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b { inputs = { input = @a } }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().Contain(d => d.Code == SpecDiagnosticCode.MissingRequiredParameter);
    }

    [Fact]
    public void Parse_ScopeMissingTarget_EmitsMissingRequiredParameter()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            scope { where = cql2("1=1") }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().Contain(d => d.Code == SpecDiagnosticCode.MissingRequiredParameter);
    }

    [Fact]
    public void Parse_ArrayLiteralPreservesOrder()
    {
        const string text = """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            map {
              layers = ["alpha", "beta", "gamma"]
            }
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        var layers = result.Document!.Map!.Layers!;
        layers.Items.Select(i => ((LiteralNode)i).String)
            .Should().ContainInConsecutiveOrder("alpha", "beta", "gamma");
    }

    [Fact]
    public void Parse_CommentsAreStrippedFromBodyButPreservedOnDocument()
    {
        const string text = """
            # header
            grammar "v1.0"

            # about to declare hospitals
            source hospitals { type = "layer", ref = "x" }

            /* trailing */
            """;

        var result = Parse(text);

        result.Diagnostics.Should().BeEmpty();
        result.Document!.Comments.Should().NotBeEmpty();
        // Each comment is keyed by a JSON-Pointer path (# disambiguates within a section).
        result.Document.Comments.Keys.Should().AllSatisfy(
            k => k.StartsWith('/').Should().BeTrue());
    }
}
