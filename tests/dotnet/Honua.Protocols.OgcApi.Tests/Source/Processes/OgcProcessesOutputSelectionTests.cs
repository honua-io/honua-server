// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

public sealed class OgcProcessesOutputSelectionTests
{
    [UnitTest]
    public void TryAddOutputBindings_NonLeadingSelection_PreservesOriginalArtifactSlot()
    {
        var definition = new ProcessDefinition
        {
            ProcessId = "test.multi-output",
            Title = "Multi-output test",
            Description = "Exercises stable output slots.",
            Category = "test",
            Parameters = [],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Table]
        };
        var requestedOutputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["outputTable"] = JsonSerializer.SerializeToElement(new { transmissionMode = "value" })
        }.ToImmutableDictionary(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        ProcessEndpoints.TryAddOutputBindings(
            metadata,
            definition,
            requestedOutputs,
            out var error).Should().BeTrue(error);

        metadata.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                $"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}1",
                "outputTable"));
    }
}
