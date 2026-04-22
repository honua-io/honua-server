// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using FluentAssertions;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;
using Honua.Core.Features.Spec.Operators;
using Honua.Core.Features.Spec.Validation;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Focused regressions for the shipped spec grammar contract.
/// These lock the documented canonical JSON shape to the actual parser,
/// validator, and canonicalizer behavior.
/// </summary>
public sealed class SpecContractRegressionTests
{
    private static readonly SpecParser Parser = new();
    private static readonly SpecCanonicalizer Canonicalizer = new();
    private static readonly SpecValidator Validator = new(new OperatorCatalog());

    [Fact]
    public async Task CanonicalJson_WithMapLayerStrings_ValidatesAgainstPublishedSchema()
    {
        const string source = """
            grammar "v1.0"
            source rivers { type = "layer", ref = "osm:waterway=river" }
            map { layers = ["rivers"] }
            """;

        var parsed = Parser.Parse(source);
        parsed.Diagnostics.Should().BeEmpty();

        var schemaJson = await File.ReadAllTextAsync(ResolveRepoPath(
            Path.Combine("docs", "developer", "spec-grammar", "v1.0", "spec.schema.json")));
        var schema = JSchema.Parse(schemaJson);
        var token = JToken.Parse(Canonicalizer.ToJson(parsed.Document!));

        token.IsValid(schema, out IList<ValidationError> errors)
            .Should().BeTrue($"published spec schema must accept canonical map layer ids: {FormatSchemaErrors(errors)}");
    }

    [Fact]
    public void Parse_CommentSidecarKeys_UseCanonicalArrayIndices()
    {
        const string source = """
            # grammar comment
            grammar "v1.0"
            # source comment
            source hospitals { type = "layer", ref = "x" }
            # scope comment
            scope { target = @hospitals }
            # compute comment
            compute nearby {
              op = buffer
              inputs = { input = @hospitals }
              params = { distance = 100.m, crs = "EPSG:3857" }
            }
            # output comment
            output results { expr = @nearby }
            """;

        var parsed = Parser.Parse(source);

        parsed.Diagnostics.Should().BeEmpty();
        parsed.Document.Should().NotBeNull();
        parsed.Document!.Comments.Keys.Should().Contain(key => key.StartsWith("/sources/0#", StringComparison.Ordinal));
        parsed.Document.Comments.Keys.Should().Contain(key => key.StartsWith("/scope/0#", StringComparison.Ordinal));
        parsed.Document.Comments.Keys.Should().Contain(key => key.StartsWith("/compute/0#", StringComparison.Ordinal));
        parsed.Document.Comments.Keys.Should().Contain(key => key.StartsWith("/outputs/0#", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RepeatedSourceIdsAcrossRuns_DoNotReuseStaleCatalogTypes()
    {
        var version = $"binding-sensitive-{Guid.NewGuid():N}";

        var first = Validate(
            """
            grammar "v1.0"
            source a { type = "layer", ref = "dataset" }
            compute slope_a { op = slope, inputs = { input = @a } }
            """,
            new RefSensitiveCatalogSnapshot(version));

        first.Diagnostics.Should().Contain(d => d.Code == SpecDiagnosticCode.TypeMismatch);

        var second = Validate(
            """
            grammar "v1.0"
            source a { type = "raster", ref = "raster" }
            compute slope_a { op = slope, inputs = { input = @a } }
            """,
            new RefSensitiveCatalogSnapshot(version));

        second.Diagnostics.Should().NotContain(d => d.Code == SpecDiagnosticCode.TypeMismatch);
        second.IsValid.Should().BeTrue();
    }

    private static SpecValidationResult Validate(string text, ISpecCatalogSnapshot catalog)
    {
        var parsed = Parser.Parse(text);
        parsed.Diagnostics.Should().BeEmpty();
        parsed.Document.Should().NotBeNull();
        return Validator.Validate(parsed.Document!, catalog);
    }

    private static string ResolveRepoPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }

    private static string FormatSchemaErrors(IList<ValidationError> errors) =>
        string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}"));

    private sealed class RefSensitiveCatalogSnapshot(string version) : ISpecCatalogSnapshot
    {
        public string Version { get; } = version;

        public TypeRef? ResolveSource(SourceBinding source)
        {
            var sourceRef = source.Properties.Fields
                .Where(field => string.Equals(field.Key, "ref", StringComparison.Ordinal))
                .Select(field => field.Value)
                .OfType<LiteralNode>()
                .Where(literal => literal.Kind == SpecTypeKind.String)
                .Select(literal => literal.String)
                .FirstOrDefault();

            return sourceRef switch
            {
                "raster" => TypeRef.Intrinsic(SpecTypeKind.Raster),
                _ => TypeRef.Intrinsic(SpecTypeKind.Dataset)
            };
        }
    }
}
