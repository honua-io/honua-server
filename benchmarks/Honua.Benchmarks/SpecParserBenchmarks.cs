// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;

namespace Honua.Benchmarks;

/// <summary>
/// Microbenchmarks for the Honua spec lex + parse pipeline. The recursive-
/// descent parser is exercised on every authoring round-trip, every
/// canonicalisation pass, and on each spec-driven analysis submission, so
/// regressions here propagate into IDE responsiveness and analysis cold-start
/// latency. We benchmark three representative document sizes — a small two-
/// section file, a medium real-world analysis (modelled on the round-trip
/// fixture), and a synthetic large spec with many compute steps — to surface
/// regressions in either the lexer's token allocation or the parser's
/// section-level synchronisation.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Spec)]
public class SpecParserBenchmarks
{
    private SpecParser _parser = null!;
    private string _smallSpec = null!;
    private string _mediumSpec = null!;
    private string _largeSpec = null!;

    [GlobalSetup]
    public void Setup()
    {
        _parser = new SpecParser();

        // Minimal, well-formed spec: header + a single source + a single
        // output. Captures the lexer and section-dispatch overhead without
        // exercising compute/scope branches.
        _smallSpec = """
            grammar "v1.0"
            kind    "analysis"
            title   "smoke spec"

            source layer_a {
              type = "layer"
              ref  = "osm:amenity=hospital"
            }

            output layer_a_features {
              expr = @layer_a
            }
            """;

        // Mirrors the canonical hospitals_near_rivers.spec fixture: two
        // sources, a scope clause, two compute steps with buffer + spatial
        // join, and a derived output. Representative of typical analyses.
        _mediumSpec = """
            grammar "v1.0"
            kind    "analysis"
            title   "hospitals within 500 m of rivers"

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

        // Synthetic worst-case-ish payload: a moderate number of sources and
        // compute steps, the kind of document the IDE has to lex on every
        // keystroke during incremental authoring.
        _largeSpec = BuildLargeSpec(sourceCount: 16, computeCount: 32);
    }

    [Benchmark(Baseline = true, Description = "Parse small spec (~10 lines)")]
    public SpecParseResult ParseSmall()
        => _parser.Parse(_smallSpec);

    [Benchmark(Description = "Parse medium spec (~30 lines, canonical fixture)")]
    public SpecParseResult ParseMedium()
        => _parser.Parse(_mediumSpec);

    [Benchmark(Description = "Parse large spec (16 sources + 32 compute steps)")]
    public SpecParseResult ParseLarge()
        => _parser.Parse(_largeSpec);

    private static string BuildLargeSpec(int sourceCount, int computeCount)
    {
        var builder = new StringBuilder(capacity: 4096);
        builder.AppendLine("grammar \"v1.0\"");
        builder.AppendLine("kind    \"analysis\"");
        builder.AppendLine("title   \"large synthetic spec\"");
        builder.AppendLine();

        for (var i = 0; i < sourceCount; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"source layer_{i} {{\n");
            builder.AppendLine("  type = \"layer\"");
            builder.Append(CultureInfo.InvariantCulture, $"  ref  = \"osm:layer_{i}\"\n");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine("scope {");
        builder.AppendLine("  target = @layer_0");
        builder.AppendLine("  where  = cql2(\"state = 'CA'\")");
        builder.AppendLine("}");
        builder.AppendLine();

        for (var i = 0; i < computeCount; i++)
        {
            var input = i % sourceCount;
            builder.Append(CultureInfo.InvariantCulture, $"compute step_{i} {{\n");
            builder.AppendLine("  op     = buffer");
            builder.Append(CultureInfo.InvariantCulture, $"  inputs = {{ input = @layer_{input} }}\n");
            builder.Append(CultureInfo.InvariantCulture, $"  params = {{ distance = {i + 100}.m, crs = \"EPSG:3857\" }}\n");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine("output final {");
        builder.AppendLine("  expr = @step_0");
        builder.AppendLine("}");

        return builder.ToString();
    }
}
