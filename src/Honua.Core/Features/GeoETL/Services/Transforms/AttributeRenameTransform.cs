// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 attribute-rename transform. Renames one attribute key to another on every
/// feature, preserving the value. Streaming and constant-memory.
/// </summary>
/// <remarks>
/// Required <see cref="TransformConfig.Options"/>:
/// <list type="bullet">
/// <item><c>from</c> — existing attribute name.</item>
/// <item><c>to</c> — new attribute name.</item>
/// </list>
/// Features that do not carry the <c>from</c> attribute pass through unchanged. The
/// stage-chain validator declares <c>from</c> as a required input field and <c>to</c> as
/// a produced output field so a downstream stage that needs <c>to</c> validates.
/// </remarks>
public sealed class AttributeRenameTransform : IPipelineTransform, ISchemaAwareTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "attribute-rename";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public TransformSchemaEffect DescribeSchema(TransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var from = RequireOption(config, "from");
        var to = RequireOption(config, "to");
        return new TransformSchemaEffect(RequiredFields: [from], ProducedFields: [to], RemovedFields: [from]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var from = RequireOption(config, "from");
        var to = RequireOption(config, "to");

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes;
            if (attributes is null || !attributes.Exists(from))
            {
                yield return feature;
                continue;
            }

            var rebuilt = new AttributesTable();
            foreach (var name in attributes.GetNames())
            {
                var key = string.Equals(name, from, StringComparison.Ordinal) ? to : name;
                rebuilt.Add(key, attributes.GetOptionalValue(name));
            }

            yield return new Feature(feature.Geometry, rebuilt);
        }
    }

    private static string RequireOption(TransformConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Attribute-rename transform requires a '{key}' option.");
        }

        return value;
    }
}
