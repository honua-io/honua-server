// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Verifies that <c>GET /api/v1/admin/capabilities</c> emits the canonical
/// <c>data.compatibility</c> envelope the generated admin SDKs handshake on, per
/// docs/developer/SDK_COMPATIBILITY_METADATA.md (controlPlaneApi / releaseChannel /
/// metadataSchemas / features) — not just the flat fields.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminCapabilitiesEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetCapabilities_EmitsDocumentedCompatibilityEnvelope()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var compatibility = document.RootElement.GetProperty("data").GetProperty("compatibility");

        compatibility.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
        compatibility.GetProperty("releaseChannel").GetString().Should().NotBeNullOrWhiteSpace();

        // controlPlaneApi.major must be the numeric major the SDK gates on first.
        var controlPlaneApi = compatibility.GetProperty("controlPlaneApi");
        controlPlaneApi.GetProperty("major").GetInt32().Should().Be(1);
        controlPlaneApi.GetProperty("basePath").GetString().Should().Be("/api/v1/admin");
        controlPlaneApi.GetProperty("deprecated").GetBoolean().Should().BeFalse();

        // metadataSchemas is an array of { version, deprecated }, newest non-deprecated usable.
        var metadataSchemas = compatibility.GetProperty("metadataSchemas");
        metadataSchemas.ValueKind.Should().Be(JsonValueKind.Array);
        metadataSchemas.GetArrayLength().Should().BeGreaterThan(0);
        metadataSchemas[0].GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        metadataSchemas[0].TryGetProperty("deprecated", out _).Should().BeTrue();

        // features advertises the manifest workflow switches, and they must match the routes actually
        // registered. Package GitOps export does not implement the removed SDK manifest operation.
        var features = compatibility.GetProperty("features");
        features.GetProperty("metadataResources").GetBoolean().Should().BeTrue();
        features.GetProperty("manifestExport").GetBoolean().Should().BeFalse();
        features.GetProperty("manifestApply").GetBoolean().Should().BeFalse();
        features.GetProperty("manifestDryRun").GetBoolean().Should().BeFalse();
        features.GetProperty("manifestPrune").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetCapabilities_AdvertisesEveryDeclaredContractVersion()
    {
        // honua-server#5378 / release ruling R27: the contract-live gate reads
        // data.compatibility.contractVersions as the whole advertised set and refuses a missing
        // key, an extra key, or a different value against release/component-versions.json.
        var declared = ReadDeclaredVersions("contractVersions");
        declared.Keys.Should().BeEquivalentTo(new[] { "admin", "metadata", "geoservices", "ogc", "stac", "grpc" });

        var response = await _fixture.Client.GetAsync("/api/v1/admin/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var compatibility = data.GetProperty("compatibility");

        ReadMap(compatibility.GetProperty("contractVersions")).Should().Equal(declared);
        ReadMap(data.GetProperty("contractVersions")).Should().Equal(declared);
        ReadMap(compatibility.GetProperty("schemaVersions")).Should().Equal(ReadDeclaredVersions("schemaVersions"));
        ReadMap(data.GetProperty("schemaVersions")).Should().Equal(ReadDeclaredVersions("schemaVersions"));

        // The pre-existing flat fields are unchanged and agree with the map.
        compatibility.GetProperty("adminApiMajor").GetString().Should().Be(declared["admin"]);
        compatibility.GetProperty("metadataApiVersion").GetString().Should().Be(declared["metadata"]);
        data.GetProperty("metadataApiVersion").GetString().Should().Be(declared["metadata"]);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_TransportContractVersionsMatchDeclaration()
    {
        // The per-transport contractVersion the manifest advertises is the same version
        // release/component-versions.json declares for that surface (#5378).
        var declared = ReadDeclaredVersions("contractVersions");

        var response = await _fixture.Client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var transports = document.RootElement.GetProperty("transports").GetProperty("items");
        foreach (var (transportId, contract) in new[] { ("geoservices-rest", "geoservices"), ("ogc-http", "ogc"), ("stac", "stac") })
        {
            var transport = transports.EnumerateArray().Single(item =>
                string.Equals(item.GetProperty("id").GetString(), transportId, StringComparison.Ordinal));
            transport.GetProperty("contractVersion").GetString().Should().Be(declared[contract], transportId);
        }
    }

    private static Dictionary<string, string> ReadDeclaredVersions(string field)
    {
        // The checked-in file the release resolver reads, not the embedded copy, so the test also
        // proves the build embedded the current declaration.
        using var declaration = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("release", "component-versions.json")));
        var versions = ReadMap(declaration.RootElement.GetProperty(field));
        new Dictionary<string, string>(field == "contractVersions"
                ? ComponentVersionsDeclaration.Embedded.ContractVersions
                : ComponentVersionsDeclaration.Embedded.SchemaVersions)
            .Should().Equal(versions);
        return versions;
    }

    private static Dictionary<string, string> ReadMap(JsonElement element)
        => element.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.GetString()!,
            StringComparer.Ordinal);
}
