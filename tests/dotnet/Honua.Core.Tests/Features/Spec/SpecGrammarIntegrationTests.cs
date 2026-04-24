// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Spec;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;
using Honua.Core.Features.Spec.Operators;
using Honua.Core.Features.Spec.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// End-to-end integration tests that drive the full parse → canonicalize →
/// validate pipeline over a realistic S1 spec and exercise
/// <see cref="ServiceCollectionExtensions.AddSpecGrammar"/>.
/// </summary>
public sealed class SpecGrammarIntegrationTests
{
    private const string FullS1Spec = """
        grammar "v1.0"
        kind    "analysis"
        title   "hospitals within 500 m of rivers"

        # Canonical reference fixture.
        source hospitals {
          type = "layer"
          ref  = "osm:amenity=hospital"
        }

        source rivers {
          type = "layer"
          ref  = "osm:waterway=river"
        }

        scope {
          target = @hospitals
          where  = cql2("state = 'CA'")
        }

        compute river_buffer {
          op     = buffer
          inputs = { input = @rivers }
          params = { distance = 500.m, crs = "EPSG:3857" }
        }

        compute at_risk {
          op     = spatial_join
          inputs = { left = @hospitals, right = @river_buffer }
          params = { crs = "EPSG:3857" }
        }

        output at_risk_features {
          expr = @at_risk
        }
        """;

    [Fact]
    public void FullS1Pipeline_ParsesCanonicalizesValidatesWithoutErrors()
    {
        var parser = new SpecParser();
        var canon = new SpecCanonicalizer();
        var validator = new SpecValidator(new OperatorCatalog());
        var catalog = new StaticSpecCatalogSnapshot(
            "integration",
            new Dictionary<string, TypeRef>(StringComparer.Ordinal)
            {
                ["hospitals"] = TypeRef.Intrinsic(SpecTypeKind.Dataset),
                ["rivers"] = TypeRef.Intrinsic(SpecTypeKind.Dataset)
            });

        var parsed = parser.Parse(FullS1Spec);
        parsed.Diagnostics.Should().BeEmpty();

        var json = canon.ToJson(parsed.Document!);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("$schema").GetString()
            .Should().Be(SpecGrammarVersion.SchemaUrl);
        doc.RootElement.GetProperty("capabilities").GetProperty("operators").GetString()
            .Should().Be(SpecGrammarVersion.CurrentOperatorCapability);
        doc.RootElement.GetProperty("sources").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("compute").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("scope").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("outputs").GetArrayLength().Should().Be(1);

        var validation = validator.Validate(parsed.Document!, catalog);
        validation.IsValid.Should().BeTrue(
            "the fixture is the canonical happy-path example; any errors indicate a regression. Diagnostics: " +
            string.Join(" | ", validation.Diagnostics.Select(d => $"{d.Code}:{d.Severity}:{d.Message}")));
    }

    [Fact]
    public void FullS1Pipeline_RoundTripsIdempotently()
    {
        var parser = new SpecParser();
        var canon = new SpecCanonicalizer();

        var document = parser.Parse(FullS1Spec).Document!;
        var first = canon.ToJson(document);
        var reparsed = SpecJsonReader.Read(first);
        var second = canon.ToJson(reparsed);

        second.Should().Be(first, "JSON → AST → JSON must be byte-for-byte idempotent");
    }

    [Fact]
    public void AddSpecGrammar_RegistersPublicInterfacesAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddSpecGrammar();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISpecParser>().Should().BeOfType<SpecParser>();
        provider.GetRequiredService<ISpecCanonicalizer>().Should().BeOfType<SpecCanonicalizer>();
        provider.GetRequiredService<ISpecValidator>().Should().BeOfType<SpecValidator>();
        provider.GetRequiredService<IOperatorCatalog>().Should().BeOfType<OperatorCatalog>();

        // Singletons — same instance across resolutions.
        var parser1 = provider.GetRequiredService<ISpecParser>();
        var parser2 = provider.GetRequiredService<ISpecParser>();
        parser1.Should().BeSameAs(parser2);
    }

    [Fact]
    public void FullS1Pipeline_JsonMatchesSchemaInvariants()
    {
        var parser = new SpecParser();
        var canon = new SpecCanonicalizer();

        var parsed = parser.Parse(FullS1Spec).Document!;
        using var json = JsonDocument.Parse(canon.ToJson(parsed));

        // Root keys are sorted.
        json.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);

        // Every source object has required id+type+ref fields.
        foreach (var source in json.RootElement.GetProperty("sources").EnumerateArray())
        {
            source.TryGetProperty("id", out _).Should().BeTrue();
            source.TryGetProperty("type", out _).Should().BeTrue();
            source.TryGetProperty("ref", out _).Should().BeTrue();
        }

        // Every compute step has required id+op fields.
        foreach (var step in json.RootElement.GetProperty("compute").EnumerateArray())
        {
            step.TryGetProperty("id", out _).Should().BeTrue();
            step.TryGetProperty("op", out _).Should().BeTrue();
        }
    }
}
