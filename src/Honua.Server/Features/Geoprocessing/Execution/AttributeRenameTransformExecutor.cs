// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>transform.attribute-rename</c> executor. Renames one attribute key to another
/// on every feature, preserving the value and geometry. Features that do not carry
/// the <c>from</c> attribute pass through unchanged. Ported from the GeoETL baseline
/// AttributeRenameTransform onto the #1185 process/executor contract.
/// </summary>
internal sealed class AttributeRenameTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.attribute-rename";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var from = inputs.Require("from");
        var to = inputs.Require("to");

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes;
            if (attributes is null || !attributes.Exists(from))
            {
                output.Add(feature);
                continue;
            }

            var rebuilt = new AttributesTable();
            foreach (var name in attributes.GetNames())
            {
                var key = string.Equals(name, from, StringComparison.Ordinal) ? to : name;
                rebuilt.Add(key, attributes.GetOptionalValue(name));
            }

            output.Add(new Feature(feature.Geometry, rebuilt));
        }

        return output;
    }
}
