// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// honua-server#5633: admin operation lists, WFS stored queries, ArcGIS migration evidence,
/// and STAC ids+CQL2 property bags used parameterless System.Text.Json overloads. Those
/// throw when JsonSerializerIsReflectionEnabledByDefault is false, which is how both the
/// AOT and JIT images run. The xUnit host leaves reflection on, so this child process is
/// the regression.
/// </summary>
public sealed class ReflectionDisabledAotJsonProbeTests
{
    [UnitTest]
    public async Task ReflectionDisabledChild_SerializesOperationsStoredQueriesEvidenceAndStacFilter()
    {
        var run = await ReflectionDisabledAotJsonProbe.RunAsync();

        run.StandardOutput.Should().Contain($"{ReflectionDisabledCloudNativeProbe.ReflectionStatePrefix}False",
            "the probe is only meaningful when the child really runs without reflection. {0}", run.Transcript);
        run.StandardOutput.Should().Contain($"{ReflectionDisabledCloudNativeProbe.CanaryPrefix}threw",
            "a reflection-based serializer call must fail in the child, as it does on the image. {0}", run.Transcript);
        run.StandardOutput.Should().Contain("active-status=200", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("type-status=200", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain($"active-operation={ReflectionDisabledAotJsonProbe.OperationId}", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("wfs-id=urn:honua:probe", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("manifest-source=arcgis-geoservices-rest", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("parity-classification=pass", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("stac-filter=true", "{0}", run.Transcript);
        run.StandardOutput.Should().Contain("stac-objectid=42", "{0}", run.Transcript);
        run.ExitCode.Should().Be(0, "the call sites must not depend on reflection metadata. {0}", run.Transcript);
    }
}
