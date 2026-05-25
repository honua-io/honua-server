// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 attribute cast / type-coerce transform. Coerces one attribute to a target CLR
/// type (<c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>, or <c>string</c>). Streaming
/// and constant-memory. A row whose value cannot be coerced is a row-level data error: it
/// is dropped from the stream rather than aborting the run, matching the ADR-0038
/// row-level-error contract (the quarantine sink captures rejected rows downstream).
/// </summary>
/// <remarks>
/// Required <see cref="TransformConfig.Options"/>:
/// <list type="bullet">
/// <item><c>field</c> — attribute name to cast.</item>
/// <item><c>to</c> — one of <c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>,
/// <c>string</c>.</item>
/// </list>
/// Optional <c>onError</c> — <c>drop</c> (default) drops uncoercible rows; <c>null</c>
/// sets the attribute to null and keeps the row; <c>keep</c> leaves the original value.
/// </remarks>
public sealed class AttributeCastTransform : IPipelineTransform, ISchemaAwareTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "attribute-cast";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public TransformSchemaEffect DescribeSchema(TransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var field = RequireOption(config, "field");
        // Validate the target type early so an unknown 'to' fails at CRUD time.
        _ = RequireOption(config, "to");
        return new TransformSchemaEffect(RequiredFields: [field], ProducedFields: [field], RemovedFields: []);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var field = RequireOption(config, "field");
        var to = RequireOption(config, "to").ToLowerInvariant();
        var onError = config.Options.TryGetValue("onError", out var rawError) ? rawError.ToLowerInvariant() : "drop";

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes;
            if (attributes is null || !attributes.Exists(field))
            {
                yield return feature;
                continue;
            }

            var raw = attributes.GetOptionalValue(field);
            if (TryCoerce(raw, to, out var coerced))
            {
                attributes[field] = coerced!;
                yield return feature;
                continue;
            }

            switch (onError)
            {
                case "null":
                    attributes[field] = null!;
                    yield return feature;
                    break;
                case "keep":
                    yield return feature;
                    break;
                default:
                    // drop: row-level error, omit the feature from the stream.
                    break;
            }
        }
    }

    private static bool TryCoerce(object? value, string to, out object? result)
    {
        result = null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);

        switch (to)
        {
            case "string":
                result = text;
                return true;
            case "int":
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    result = i;
                    return true;
                }

                return false;
            case "long":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    result = l;
                    return true;
                }

                return false;
            case "double":
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    result = d;
                    return true;
                }

                return false;
            case "bool":
                if (bool.TryParse(text, out var b))
                {
                    result = b;
                    return true;
                }

                if (string.Equals(text, "1", StringComparison.Ordinal))
                {
                    result = true;
                    return true;
                }

                if (string.Equals(text, "0", StringComparison.Ordinal))
                {
                    result = false;
                    return true;
                }

                return false;
            default:
                throw new InvalidOperationException(
                    $"Attribute-cast target type '{to}' is not supported. " +
                    "Supported: int, long, double, bool, string.");
        }
    }

    private static string RequireOption(TransformConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Attribute-cast transform requires a '{key}' option.");
        }

        return value;
    }
}
