// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Protects Esri function-name uniqueness and established canonical routes.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerTaskNameTests
{
    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void PublishedParameters_UnsafeNamesAndEncodedLookingNames_RoundTripWithoutCollisions()
    {
        string[] names = ["from", "class", "gis", "future", "estimate", "a.b", "a-b", "9input", "HonuaParameter_66726F6D", "wkb", "layerId"];
        var template = new BuiltInProcessCatalog().GetProcess("geometry.buffer")!;
        var definition = template with
        {
            Parameters = names.Select(name => template.Parameters[0] with { Name = name }).ToArray()
        };
        var prefix = GPServerParameterNames.GetEncodingPrefix(definition);
        var published = names.Select(name => GPServerParameterNames.Publish(name, prefix)).ToArray();
        published.Select(name => name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
        published.Should().Contain("wkb").And.Contain("layerId").And.Contain("HonuaParameter_66726F6D");
        foreach (var name in published)
        {
            name.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
        }
        published.Select(name => GPServerParameterNames.Resolve(name.ToLowerInvariant(), definition, prefix)).Should().Equal(names);
        names.Select(name => GPServerParameterNames.Resolve(name, definition, prefix)).Should().Equal(names);
        GPServerParameterNames.Resolve("unknown", definition, prefix).Should().Be("unknown");
        GPServerParameterNames.GetEncodingPrefix(definition with { Parameters = definition.Parameters.Reverse().ToArray() })
            .Should().Be(prefix);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public void PublishedNames_PunctuationKeywordsCaseAndEncodedLookingIds_RemainDistinct()
    {
        string[] ids = ["a.b", "a-b", "a_b", "class", "Class", "9task", "Honua_612E62", "Buffer", "geometry.buffer"];
        var template = new BuiltInProcessCatalog().GetProcess("geometry.buffer")!;
        var definitions = ids.Select(id => template with { ProcessId = id }).ToArray();
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.ListProcesses().Returns(definitions);
        catalog.GetProcess(Arg.Any<string>()).Returns(call => definitions.SingleOrDefault(p => p.ProcessId == call.Arg<string>()));

        var names = GPServerEndpoints.BuildPublishedTaskNames(catalog).ToArray();
        names.Should().HaveCount(ids.Length, "the real Buffer process suppresses the conventional alias");
        names.Select(name => name.ToLowerInvariant()).Should().OnlyHaveUniqueItems("the SDK lowercases names containing underscores");
        foreach (var name in names)
        {
            name.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
            ids.Should().NotContain(name, "generated routes must not hijack an existing canonical route");
        }
        names.Select(name => GPServerEndpoints.ResolveTaskDefinition(catalog, name)!.ProcessId).Should().Equal(ids);
        foreach (var definition in definitions)
        {
            GPServerEndpoints.ResolveTaskDefinition(catalog, definition.ProcessId).Should().BeSameAs(definition);
        }
        catalog.ListProcesses().Returns(definitions.Reverse().ToArray());
        GPServerEndpoints.BuildPublishedTaskNames(catalog).Should().BeEquivalentTo(names, "catalog enumeration order must not change names");
    }
}
