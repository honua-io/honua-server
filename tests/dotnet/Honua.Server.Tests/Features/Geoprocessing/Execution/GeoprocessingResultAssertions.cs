// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

internal static class GeoprocessingResultAssertions
{
    internal static void AssertInlineValueMatchesArtifact(
        JsonDocument results,
        AnalysisResultPackage package,
        string jobId)
    {
        var artifact = package.Artifacts.Should().ContainSingle().Which;
        var isScalar = artifact.Label == "outputScalar";
        artifact.ArtifactId.Should().Be($"{jobId}:artifact:1");
        artifact.Kind.ToString().Should().Be(isScalar ? "Scalar" : "FeatureLayer");
        artifact.ContentType.Should().Be(isScalar ? "application/json" : "application/geo+json");
        var prefix = $"data:{artifact.ContentType};base64,";
        artifact.Uri.Should().StartWith(prefix);
        var stored = JsonNode.Parse(Convert.FromBase64String(artifact.Uri![prefix.Length..]));
        var output = results.RootElement.GetProperty(artifact.Label!);
        JsonNode.DeepEquals(stored, JsonNode.Parse(output.GetRawText())).Should().BeTrue(
            "OGC value transmission must preserve the complete canonical artifact payload");
    }
}
