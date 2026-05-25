// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 dedup transform. Emits the first feature for each distinct key and drops later
/// duplicates. The key is built from one or more attribute fields, the geometry, or both.
/// Pure managed — no native dependency. The transform keeps a hash set of seen keys, so
/// memory grows with the number of distinct keys rather than the total feature count.
/// </summary>
/// <remarks>
/// Key composition (at least one required):
/// <list type="bullet">
/// <item><c>keys</c> — comma-separated attribute field names whose values form the key.
/// </item>
/// <item><c>geometry</c> — <c>true</c> to include the geometry (its normalized WKT) in the
/// key.</item>
/// </list>
/// When both are present the key combines the attribute values and the geometry. When
/// neither is present the transform throws at configuration time.
/// </remarks>
public sealed class DedupTransform : IPipelineTransform, ISchemaAwareTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "dedup";

    // Unit-separator control char between key parts so distinct field boundaries never
    // collide (for example {"ab","c"} versus {"a","bc"}).
    private const char Separator = '\u001F';
    private const char NullMarker = '\u00A0';

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public TransformSchemaEffect DescribeSchema(TransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (keys, _) = ReadKeySpec(config);
        return new TransformSchemaEffect(RequiredFields: keys, ProducedFields: [], RemovedFields: []);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var (keys, useGeometry) = ReadKeySpec(config);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(feature, keys, useGeometry);
            if (seen.Add(key))
            {
                yield return feature;
            }
        }
    }

    private static string BuildKey(IFeature feature, IReadOnlyList<string> keys, bool useGeometry)
    {
        var builder = new StringBuilder();
        var attributes = feature.Attributes;

        foreach (var field in keys)
        {
            var value = attributes is not null && attributes.Exists(field)
                ? Convert.ToString(attributes.GetOptionalValue(field), CultureInfo.InvariantCulture)
                : null;
            if (value is null)
            {
                builder.Append(NullMarker);
            }
            else
            {
                builder.Append(value);
            }

            builder.Append(Separator);
        }

        if (useGeometry)
        {
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                builder.Append(NullMarker);
            }
            else
            {
                var normalized = geometry.Normalized();
                builder.Append(normalized.AsText());
            }
        }

        return builder.ToString();
    }

    private static (IReadOnlyList<string> Keys, bool UseGeometry) ReadKeySpec(TransformConfig config)
    {
        var useGeometry = config.Options.TryGetValue("geometry", out var rawGeometry)
            && bool.TryParse(rawGeometry, out var parsed) && parsed;

        string[] keys = [];
        if (config.Options.TryGetValue("keys", out var rawKeys) && !string.IsNullOrWhiteSpace(rawKeys))
        {
            keys = rawKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (keys.Length == 0 && !useGeometry)
        {
            throw new InvalidOperationException(
                "Dedup transform requires a 'keys' attribute list, 'geometry=true', or both.");
        }

        return (keys, useGeometry);
    }
}
