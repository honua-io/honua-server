// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

// Edits are only accepted where the bound storage declares edit support; a read-only source
// binding must not be published with edit capabilities.
public sealed class MetadataV2PublicationEditStorageValidationTests
{
    private const string Graph = """
        {"services":[{"metadata":{"id":"service","name":"service"}}],
         "resources":[{"metadata":{"id":"dataset","name":"dataset"},"storageBindingIds":["binding"]}],
         "connections":[{"metadata":{"id":"connection"},"type":"managed"}],
         "storageBindings":[{"metadata":{"id":"binding"},"resourceId":"dataset","connectionId":"connection",
           "storageLayerId":17,"capabilities":["query","filter"]}],
         "publications":[{"metadata":{"id":"layer"},"resourceId":"dataset","serviceId":"service"}]}
        """;

    [UnitTheory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Delete")]
    public void PublicationEditOverReadOnlyBinding_IsRejected(string capability)
    {
        var result = Validate(publicationCapabilities: ["Query", capability]);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Contains("publication 'layer'", StringComparison.Ordinal)
            && error.Contains("edit support", StringComparison.Ordinal));
    }

    [UnitTest]
    public void ServiceEditCapabilitiesOverReadOnlyBinding_AreRejected()
    {
        var result = Validate(serviceCapabilities: ["Query", "Update"]);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("publication 'layer'", StringComparison.Ordinal));
    }

    [UnitTest]
    public void PublicationEditOverEditableBinding_IsAccepted()
        => Validate(publicationCapabilities: ["Query", "Create", "Update", "Delete"], bindingCapabilities: ["query", "edit"])
            .Errors.Should().BeEmpty();

    [UnitTest]
    public void QueryOnlyPublicationOverReadOnlyBinding_IsAccepted()
        => Validate(publicationCapabilities: ["Query", "Extract"]).Errors.Should().BeEmpty();

    [UnitTest]
    public void PublicationEditNamingAnUnknownBinding_IsRejected()
    {
        var node = Parse();
        node["publications"]![0]!["capabilities"] = new JsonArray("Query", "Create");
        node["publications"]![0]!["storageBindingId"] = "missing";

        MetadataV2GraphValidator.Validate(Deserialize(node)).Errors
            .Should().Contain(error => error.Contains("publication 'layer'", StringComparison.Ordinal)
                && error.Contains("edit support", StringComparison.Ordinal));
    }

    private static MetadataV2GraphValidationResult Validate(
        string[]? publicationCapabilities = null,
        string[]? serviceCapabilities = null,
        string[]? bindingCapabilities = null)
    {
        var node = Parse();
        if (publicationCapabilities is not null)
        {
            node["publications"]![0]!["capabilities"] = ToArray(publicationCapabilities);
        }
        if (serviceCapabilities is not null)
        {
            node["services"]![0]!["options"] = new JsonObject { ["capabilities"] = ToArray(serviceCapabilities) };
        }
        if (bindingCapabilities is not null)
        {
            node["storageBindings"]![0]!["capabilities"] = ToArray(bindingCapabilities);
        }
        return MetadataV2GraphValidator.Validate(Deserialize(node));
    }

    private static JsonNode Parse() => JsonNode.Parse(Graph)!;

    private static JsonArray ToArray(string[] values) => new([.. values.Select(static value => (JsonNode?)value)]);

    private static MetadataV2Graph Deserialize(JsonNode node)
        => JsonSerializer.Deserialize(node.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!;
}
