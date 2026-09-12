// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Covers the shared edit-capability resolver the Esri GeoServices and OGC API Features write
/// surfaces both consult (honua-server#4073, honua-server#4707).
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2EditCapabilitiesTests
{
    private static MetadataV2Service ServiceWithCapabilities(params string[] capabilities)
        => new()
        {
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(capabilities)
            }
        };

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveDeclared_PublicationCapabilities_WinOverService()
    {
        var service = ServiceWithCapabilities("Query", "Create", "Update", "Delete");
        var publication = new MetadataV2Publication { Capabilities = ["Query", "Extract"] };

        MetadataV2EditCapabilities.ResolveDeclared(service, publication)
            .Should().BeEquivalentTo(["Query", "Extract"]);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveDeclared_EmptyPublicationCapabilities_FallsBackToService()
    {
        var service = ServiceWithCapabilities("Query", "Create");
        var publication = new MetadataV2Publication();

        MetadataV2EditCapabilities.ResolveDeclared(service, publication)
            .Should().BeEquivalentTo(["Query", "Create"]);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveDeclared_NothingDeclaredAnywhere_ReturnsNull()
    {
        MetadataV2EditCapabilities
            .ResolveDeclared(new MetadataV2Service(), new MetadataV2Publication())
            .Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Resolve_NothingDeclaredAnywhere_FallsBackToQueryOnly()
    {
        MetadataV2EditCapabilities
            .Resolve(new MetadataV2Service(), new MetadataV2Publication())
            .Should().BeEquivalentTo(["Query"]);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Supports_DefaultPublishCapabilities_RejectsCreate()
    {
        // ["Query","Extract"] is what PostgreSqlLayerPublishingService stamps on every
        // publication it creates, and it is exactly the set that used to let an OGC API
        // Features insert through unannounced (#4707).
        var service = new MetadataV2Service();
        var publication = new MetadataV2Publication { Capabilities = ["Query", "Extract"] };

        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create)
            .Should().BeFalse();
        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Update)
            .Should().BeFalse();
        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Delete)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Supports_EditingUmbrellaToken_CoversEveryEditKind()
    {
        var service = new MetadataV2Service();
        var publication = new MetadataV2Publication { Capabilities = ["Query", "Editing"] };

        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create)
            .Should().BeTrue();
        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Update)
            .Should().BeTrue();
        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Delete)
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Supports_CapabilityTokenComparisonIgnoresCase()
    {
        var service = new MetadataV2Service();
        var publication = new MetadataV2Publication { Capabilities = ["query", "create"] };

        MetadataV2EditCapabilities.Supports(service, publication, MetadataV2EditCapabilities.Create)
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsDeclared_DeclaredSetOmittingOperation_IsFalse()
    {
        var service = new MetadataV2Service();
        var publication = new MetadataV2Publication { Capabilities = ["Query", "Extract"] };

        MetadataV2EditCapabilities
            .SupportsDeclared(service, publication, MetadataV2EditCapabilities.Create)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsDeclared_DeclaredSetContainingOperation_IsTrue()
    {
        var service = ServiceWithCapabilities("Query", "Create");

        MetadataV2EditCapabilities
            .SupportsDeclared(service, new MetadataV2Publication(), MetadataV2EditCapabilities.Create)
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SupportsDeclared_NoDeclarationAnywhere_IsNull()
    {
        // The v1-compatibility snapshot projects no capabilities onto OGC API Features
        // publications. Enforcement must read that silence as "no statement" rather than as
        // a denial, or editing disappears for deployments that never opted out of it.
        MetadataV2EditCapabilities
            .SupportsDeclared(new MetadataV2Service(), new MetadataV2Publication(), MetadataV2EditCapabilities.Create)
            .Should().BeNull();
    }
}
