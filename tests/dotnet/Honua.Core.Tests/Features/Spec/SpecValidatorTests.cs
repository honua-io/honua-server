// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;
using Honua.Core.Features.Spec.Operators;
using Honua.Core.Features.Spec.Validation;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Unit tests for <see cref="SpecValidator"/> — covers every diagnostic code
/// from the ticket 788 acceptance list (unknown refs, type mismatches,
/// missing required params, unknown operators, CRS warnings, grammar
/// version incompatibility, plus the catalog-unavailable fallback).
/// </summary>
public sealed class SpecValidatorTests
{
    private static readonly SpecParser _parser = new();
    private static readonly SpecValidator _validator = new(new OperatorCatalog());

    private static SpecValidationResult Validate(string text, ISpecCatalogSnapshot? catalog = null)
    {
        var parsed = _parser.Parse(text);
        parsed.Document.Should().NotBeNull();
        return _validator.Validate(parsed.Document!, catalog);
    }

    private static StaticSpecCatalogSnapshot BuildCatalog(params (string Id, SpecTypeKind Kind)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Id,
            e => TypeRef.Intrinsic(e.Kind),
            StringComparer.Ordinal);
        return new StaticSpecCatalogSnapshot($"test-{entries.Length}-{Guid.NewGuid()}", dict);
    }

    [Fact]
    public void Validate_HappyPath_HasNoErrors()
    {
        var catalog = BuildCatalog(
            ("hospitals", SpecTypeKind.Dataset),
            ("rivers", SpecTypeKind.Dataset));

        var result = Validate(
            """
            grammar "v1.0"
            source hospitals { type = "layer", ref = "osm:a" }
            source rivers    { type = "layer", ref = "osm:b" }
            compute river_buffer {
              op = buffer
              inputs = { input = @rivers }
              params = { distance = 500.m, crs = "EPSG:3857" }
            }
            compute near {
              op = spatial_join
              inputs = { left = @hospitals, right = @river_buffer }
              params = { crs = "EPSG:3857" }
            }
            output o { expr = @near }
            """,
            catalog);

        result.Diagnostics.Where(d => d.Severity == SpecDiagnosticSeverity.Error).Should().BeEmpty();
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_UnknownReference_Reports()
    {
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output o { expr = @missing }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.UnknownReference &&
            d.Severity == SpecDiagnosticSeverity.Error &&
            d.Message.Contains("@missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateSourceId_Reports()
    {
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            source a { type = "layer", ref = "y" }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d => d.Code == SpecDiagnosticCode.DuplicateIdentifier);
    }

    [Fact]
    public void Validate_UnknownOperator_Reports()
    {
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b { op = does_not_exist, inputs = { input = @a } }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d => d.Code == SpecDiagnosticCode.UnknownOperator);
    }

    [Fact]
    public void Validate_MissingRequiredParam_OnBuffer_Reports()
    {
        // buffer requires a distance parameter.
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b { op = buffer, inputs = { input = @a } }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.MissingRequiredParameter &&
            d.Message.Contains("distance", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TypeMismatch_DistanceWhereStringExpected()
    {
        // filter.where expects a string but we pass a distance literal.
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute f { op = filter, inputs = { input = @a }, params = { where = 5.km } }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.TypeMismatch &&
            d.Severity == SpecDiagnosticSeverity.Error);
    }

    [Fact]
    public void Validate_CrsWarning_WhenDistanceHasNoCrs()
    {
        // Buffer is CrsSensitive; when no crs is declared we expect a warning.
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b { op = buffer, inputs = { input = @a }, params = { distance = 500.m } }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.CrsUnitMismatch &&
            d.Severity == SpecDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Validate_CrsError_WhenGeographicCrsPairedWithDistance()
    {
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            compute b {
              op = buffer
              inputs = { input = @a }
              params = { distance = 500.m, crs = "EPSG:4326" }
            }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.CrsUnitMismatch &&
            d.Severity == SpecDiagnosticSeverity.Error);
    }

    [Fact]
    public void Validate_UnsupportedGrammarVersion_MajorDrift_Reports()
    {
        var result = Validate(
            """
            grammar "v2.0"
            source a { type = "layer", ref = "x" }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.UnsupportedGrammarVersion);
    }

    [Fact]
    public void Validate_UnsupportedGrammarVersion_MinorDrift_Reports()
    {
        var result = Validate(
            """
            grammar "v1.99"
            source a { type = "layer", ref = "x" }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.UnsupportedGrammarVersion);
    }

    [Fact]
    public void Validate_MissingGrammarDirective_Reports()
    {
        var result = Validate(
            """
            source a { type = "layer", ref = "x" }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        result.Diagnostics.Should().Contain(d =>
            d.Code == SpecDiagnosticCode.UnsupportedGrammarVersion);
    }

    [Fact]
    public void Validate_CatalogUnavailable_EmitsSingleWarning()
    {
        // When no catalog is supplied, we fall back to the Empty snapshot and
        // emit a single CatalogUnavailable warning, not one per source.
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            source b { type = "layer", ref = "y" }
            """);

        result.Diagnostics
            .Where(d => d.Code == SpecDiagnosticCode.CatalogUnavailable)
            .Should().HaveCount(1, "a single warning is sufficient — per-source noise obscures real errors");
    }

    [Fact]
    public void Validate_DiagnosticsCarryLineAndColumn()
    {
        var result = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "x" }
            output o { expr = @missing }
            """,
            BuildCatalog(("a", SpecTypeKind.Dataset)));

        var unknown = result.Diagnostics.Single(d => d.Code == SpecDiagnosticCode.UnknownReference);
        unknown.Span.Line.Should().BeGreaterThan(0);
        unknown.Span.Column.Should().BeGreaterThan(0);
    }
}
