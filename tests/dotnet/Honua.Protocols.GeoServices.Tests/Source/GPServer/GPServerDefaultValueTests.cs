// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Checks the JSON wire types consumed by Esri's toolbox importer.</summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerDefaultValueTests
{
    [Theory]
    [InlineData(ProcessParameterValueType.Flag, "false", "false")]
    [InlineData(ProcessParameterValueType.Flag, "true", "true")]
    [InlineData(ProcessParameterValueType.WholeNumber, "250000", "250000")]
    [InlineData(ProcessParameterValueType.FloatingPoint, "1.25", "1.25")]
    [InlineData(ProcessParameterValueType.Srid, "3857", "3857")]
    [InlineData(ProcessParameterValueType.Text, "false", "\"false\"")]
    [InlineData(ProcessParameterValueType.Text, "", "\"\"")]
    [InlineData(ProcessParameterValueType.Flag, null, "null")]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void TaskInfo_DefaultValue_PreservesWireTypeAndCanonicalTextRegardlessOfCulture(
        ProcessParameterValueType type, string? canonicalDefault, string expectedJson)
    {
        var template = new BuiltInProcessCatalog().GetProcess("geometry.buffer")!;
        var parameter = template.Parameters[0] with { ValueType = type, DefaultValue = canonicalDefault };
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var metadata = GPServerEndpoints.BuildTaskInfo("Buffer", template with { Parameters = [parameter] });
            metadata.Parameters.Should().NotBeNull();
            (metadata.Parameters![0].DefaultValue?.GetRawText() ?? "null").Should().Be(expectedJson);
            parameter.DefaultValue.Should().Be(canonicalDefault, "the canonical and OGC contract remains textual");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
