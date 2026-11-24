// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Core.Tests.Features.Publishing.Content;

[Protocol(ProtocolNames.TestQuality)]
public sealed class ContentPublicationVocabularyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [UnitTest]
    [Operation(Operations.Query)]
    public void AppKind_UsesCanonicalModelAndJsonName()
    {
        var kind = Enum.Parse<ContentPublicationKind>("App");
        Assert.Equal("\"app\"", JsonSerializer.Serialize(kind, JsonOptions));
        Assert.Equal(kind, JsonSerializer.Deserialize<ContentPublicationKind>("\"app\"", JsonOptions));
    }

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Theory]
    [InlineData("Dataset", "dataset")]
    [InlineData("Layer", "layer")]
    [InlineData("Map", "map")]
    [InlineData("App", "app")]
    public void DependencyKind_UsesCanonicalModelAndJsonName(string modelName, string jsonName)
    {
        var kind = Enum.Parse<ContentPublicationDependencyKind>(modelName);
        Assert.Equal($"\"{jsonName}\"", JsonSerializer.Serialize(kind, JsonOptions));
        Assert.Equal(kind, JsonSerializer.Deserialize<ContentPublicationDependencyKind>($"\"{jsonName}\"", JsonOptions));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AppKind_RejectsRetiredPublicName()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ContentPublicationKind>("\"generated-app\"", JsonOptions));
    }

    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Theory]
    [InlineData("resource")]
    [InlineData("publication")]
    [InlineData("map-package")]
    [InlineData("app-package")]
    public void DependencyKind_RejectsRetiredPublicName(string jsonName)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ContentPublicationDependencyKind>($"\"{jsonName}\"", JsonOptions));
    }
}
