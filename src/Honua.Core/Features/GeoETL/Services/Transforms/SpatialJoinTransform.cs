// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Spatial-join transform. For each streamed (target) feature it queries the
/// reference (join) layer through an in-memory <see cref="STRtree{T}"/> spatial
/// index and either transfers attributes from the first match (the enrichment
/// mode, default) or — in AGGREGATE mode — summarizes EVERY matched join feature
/// into per-target statistics (the ArcGIS <c>SpatialJoin_analysis</c>
/// one-to-one summarizing form). Pure managed NetTopologySuite — no GEOS native
/// dependency. The streamed (left) side stays constant-memory; only the reference
/// (right) side is materialized, which suits the "small lookup polygons, large
/// feature stream" shape (ADR-0038 delegates large spatial joins to PostGIS).
/// </summary>
/// <remarks>
/// Reference set (one required): <c>referenceInline</c> (a GeoJSON FeatureCollection
/// document) or <c>referencePath</c> (a GeoJSON file path). Predicate:
/// <list type="bullet">
/// <item><c>predicate</c> — <c>intersects</c> (default), <c>contains</c> (the
/// reference geometry must contain the feature, the point-in-polygon case), or
/// <c>within</c> (the feature must contain the reference geometry).</item>
/// </list>
/// Enrichment mode (default) options:
/// <list type="bullet">
/// <item><c>transfer</c> — comma-separated reference attribute names to copy; when
/// omitted all reference attributes are copied.</item>
/// <item><c>prefix</c> — prepended to each transferred attribute name to avoid
/// collisions.</item>
/// <item><c>keepUnmatched</c> — <c>true</c> (default) passes unmatched features
/// through unchanged; <c>false</c> drops them (inner join).</item>
/// </list>
/// Aggregate mode (set <c>aggregate=true</c>):
/// <list type="bullet">
/// <item><c>statistics</c> — semicolon-separated <c>field:stat</c> pairs over the
/// matched JOIN features. Supported: <c>count</c> (emitted as <c>JOIN_COUNT</c>),
/// <c>sum</c>, <c>mean</c>, <c>min</c>, <c>max</c> on numeric join fields, named
/// <c>STAT_field</c>. A target with zero matches keeps a <c>JOIN_COUNT</c> of 0
/// and null aggregates — emitted one-to-one (every target is preserved).</item>
/// </list>
/// </remarks>
public sealed class SpatialJoinTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "spatial-join";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var predicate = ReadPredicate(config);
        var aggregate = config.Options.TryGetValue("aggregate", out var rawAggregate)
            && bool.TryParse(rawAggregate, out var parsedAggregate) && parsedAggregate;

        var index = await BuildIndexAsync(config, cancellationToken).ConfigureAwait(false);

        if (aggregate)
        {
            var stats = ReadStatistics(config);
            await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // One-to-one: every target is preserved with its match summary,
                // even when zero join features match (JOIN_COUNT = 0).
                yield return Summarize(feature, index, predicate, stats);
            }

            yield break;
        }

        var prefix = config.Options.TryGetValue("prefix", out var rawPrefix) ? rawPrefix : string.Empty;
        var keepUnmatched = !config.Options.TryGetValue("keepUnmatched", out var rawKeep)
            || !bool.TryParse(rawKeep, out var keep) || keep;
        string[]? transfer = config.Options.TryGetValue("transfer", out var rawTransfer)
            && !string.IsNullOrWhiteSpace(rawTransfer)
            ? rawTransfer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                if (keepUnmatched)
                {
                    yield return feature;
                }

                continue;
            }

            var match = FindFirstMatch(index, geometry, predicate);
            if (match is null)
            {
                if (keepUnmatched)
                {
                    yield return feature;
                }

                continue;
            }

            yield return Enrich(feature, match.Attributes, transfer, prefix);
        }
    }

    private static Feature Summarize(
        IFeature target,
        STRtree<IFeature> index,
        SpatialPredicate predicate,
        IReadOnlyList<StatSpec> stats)
    {
        var merged = new AttributesTable();
        if (target.Attributes is not null)
        {
            foreach (var name in target.Attributes.GetNames())
            {
                merged.Add(name, target.Attributes.GetOptionalValue(name));
            }
        }

        var accumulator = new StatAccumulator(stats);
        var geometry = target.Geometry;
        if (geometry is not null && !geometry.IsEmpty)
        {
            foreach (var candidate in index.Query(geometry.EnvelopeInternal))
            {
                if (Matches(candidate.Geometry, geometry, predicate))
                {
                    accumulator.Add(candidate);
                }
            }
        }

        // JOIN_COUNT is always emitted so a zero-match target is distinguishable.
        Upsert(merged, "JOIN_COUNT", accumulator.Count);
        foreach (var spec in stats)
        {
            if (spec.Kind == StatKind.Count)
            {
                continue; // JOIN_COUNT already covers the match count.
            }

            var (name, value) = accumulator.Resolve(spec);
            Upsert(merged, name, value);
        }

        return new Feature(target.Geometry, merged);
    }

    private static void Upsert(AttributesTable table, string name, object? value)
    {
        if (table.Exists(name))
        {
            table[name] = value;
        }
        else
        {
            table.Add(name, value);
        }
    }

    private static Feature Enrich(
        IFeature feature,
        IAttributesTable? referenceAttributes,
        string[]? transfer,
        string prefix)
    {
        var merged = new AttributesTable();
        if (feature.Attributes is not null)
        {
            foreach (var name in feature.Attributes.GetNames())
            {
                merged.Add(name, feature.Attributes.GetOptionalValue(name));
            }
        }

        if (referenceAttributes is not null)
        {
            var names = transfer ?? referenceAttributes.GetNames();
            foreach (var name in names)
            {
                if (!referenceAttributes.Exists(name))
                {
                    continue;
                }

                var key = string.IsNullOrEmpty(prefix) ? name : prefix + name;
                if (merged.Exists(key))
                {
                    merged[key] = referenceAttributes.GetOptionalValue(name);
                }
                else
                {
                    merged.Add(key, referenceAttributes.GetOptionalValue(name));
                }
            }
        }

        return new Feature(feature.Geometry, merged);
    }

    private static IFeature? FindFirstMatch(STRtree<IFeature> index, NtsGeometry geometry, SpatialPredicate predicate)
    {
        var candidates = index.Query(geometry.EnvelopeInternal);
        foreach (var candidate in candidates)
        {
            if (Matches(candidate.Geometry, geometry, predicate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Matches(NtsGeometry? referenceGeometry, NtsGeometry targetGeometry, SpatialPredicate predicate)
    {
        if (referenceGeometry is null)
        {
            return false;
        }

        return predicate switch
        {
            // contains: the reference (join) geometry contains the target —
            // the classic point-in-polygon case.
            SpatialPredicate.Contains => referenceGeometry.Contains(targetGeometry),
            // within: the target contains the reference geometry.
            SpatialPredicate.Within => targetGeometry.Contains(referenceGeometry),
            _ => referenceGeometry.Intersects(targetGeometry),
        };
    }

    private static SpatialPredicate ReadPredicate(TransformConfig config)
    {
        if (!config.Options.TryGetValue("predicate", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return SpatialPredicate.Intersects;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "intersects" => SpatialPredicate.Intersects,
            "contains" => SpatialPredicate.Contains,
            "within" => SpatialPredicate.Within,
            _ => throw new InvalidOperationException(
                $"Spatial-join transform does not support predicate '{raw}'. " +
                "Supported: intersects, contains, within."),
        };
    }

    private static List<StatSpec> ReadStatistics(TransformConfig config)
    {
        if (!config.Options.TryGetValue("statistics", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var specs = new List<StatSpec>();
        foreach (var token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            var statName = (parts.Length >= 2 ? parts[1] : parts[0]).ToLowerInvariant();
            var field = parts.Length >= 2 ? parts[0] : string.Empty;

            var kind = statName switch
            {
                "count" => StatKind.Count,
                "sum" => StatKind.Sum,
                "mean" or "avg" or "average" => StatKind.Mean,
                "min" => StatKind.Min,
                "max" => StatKind.Max,
                _ => throw new InvalidOperationException(
                    $"Spatial-join aggregate does not support statistic '{statName}'. " +
                    "Supported: count, sum, mean, min, max."),
            };

            if (kind != StatKind.Count && string.IsNullOrWhiteSpace(field))
            {
                throw new InvalidOperationException(
                    $"Spatial-join statistic '{statName}' requires a join field, e.g. 'fieldName:{statName}'.");
            }

            specs.Add(new StatSpec(kind, field, OutputName(kind, field)));
        }

        return specs;
    }

    private static string OutputName(StatKind kind, string field) => kind switch
    {
        StatKind.Count => "JOIN_COUNT",
        StatKind.Sum => "SUM_" + field,
        StatKind.Mean => "MEAN_" + field,
        StatKind.Min => "MIN_" + field,
        StatKind.Max => "MAX_" + field,
        _ => field,
    };

    private static bool TryReadNumeric(IFeature feature, string field, out double value)
    {
        value = 0;
        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(field))
        {
            return false;
        }

        var raw = attributes.GetOptionalValue(field);
        switch (raw)
        {
            case null:
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case short s:
                value = s;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }
    }

    private enum SpatialPredicate
    {
        Intersects,
        Contains,
        Within,
    }

    private enum StatKind
    {
        Count,
        Sum,
        Mean,
        Min,
        Max,
    }

    private readonly record struct StatSpec(StatKind Kind, string Field, string OutputName);

    private sealed class StatAccumulator
    {
        private readonly Dictionary<string, double> _sums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _mins = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _maxs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fields;

        public StatAccumulator(IReadOnlyList<StatSpec> stats)
        {
            _fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in stats)
            {
                if (spec.Kind != StatKind.Count && !string.IsNullOrWhiteSpace(spec.Field))
                {
                    _fields.Add(spec.Field);
                }
            }
        }

        public long Count { get; private set; }

        public void Add(IFeature joinFeature)
        {
            Count++;
            foreach (var field in _fields)
            {
                if (TryReadNumeric(joinFeature, field, out var numeric))
                {
                    _sums[field] = _sums.GetValueOrDefault(field) + numeric;
                    _counts[field] = _counts.GetValueOrDefault(field) + 1;
                    _mins[field] = _mins.TryGetValue(field, out var min) ? Math.Min(min, numeric) : numeric;
                    _maxs[field] = _maxs.TryGetValue(field, out var max) ? Math.Max(max, numeric) : numeric;
                }
            }
        }

        public (string Name, object? Value) Resolve(StatSpec spec)
        {
            switch (spec.Kind)
            {
                case StatKind.Sum:
                    return (spec.OutputName, _sums.TryGetValue(spec.Field, out var sum) ? sum : null);
                case StatKind.Mean:
                    return (spec.OutputName,
                        _counts.TryGetValue(spec.Field, out var c) && c > 0 ? _sums[spec.Field] / c : null);
                case StatKind.Min:
                    return (spec.OutputName, _mins.TryGetValue(spec.Field, out var min) ? min : null);
                case StatKind.Max:
                    return (spec.OutputName, _maxs.TryGetValue(spec.Field, out var max) ? max : null);
                default:
                    return (spec.OutputName, Count);
            }
        }
    }

    private static async Task<STRtree<IFeature>> BuildIndexAsync(
        TransformConfig config,
        CancellationToken cancellationToken)
    {
        var index = new STRtree<IFeature>();
        var reader = new StreamingGeoJsonReader();

        await using var stream = OpenReferenceStream(config);
        await foreach (var feature in reader.ReadFeaturesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is not null && !feature.Geometry.IsEmpty)
            {
                index.Insert(feature.Geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        return index;
    }

    private static Stream OpenReferenceStream(TransformConfig config)
    {
        if (config.Options.TryGetValue("referenceInline", out var inline) && !string.IsNullOrEmpty(inline))
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(inline), writable: false);
        }

        if (config.Options.TryGetValue("referencePath", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536,
                useAsync: true);
        }

        throw new InvalidOperationException(
            "Spatial-join transform requires a 'referenceInline' GeoJSON document or a 'referencePath' option.");
    }
}
