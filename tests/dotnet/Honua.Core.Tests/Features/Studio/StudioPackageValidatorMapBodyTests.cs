// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Map-family body gate against the published portable <c>honua_map_package.v1</c> schema
/// (honua-server#4898). Before the fix the validator deserialized the body into the
/// geoprocessing <c>MapPackage</c> record, so a schema-valid camera-form
/// <c>initialView</c>, a non-<c>url</c> locator and the SDK's canonical artifact were all
/// refused with one opaque <c>studio.body.invalid</c> diagnostic at <c>/body</c>.
/// </summary>
public sealed class StudioPackageValidatorMapBodyTests
{
    /// <summary>
    /// The base body of the honua-server#4898 replay table: every row adds one member.
    /// </summary>
    private const string ReplayBaseMembers = """
        "mapPackageId": "map-4898",
        "format": "honua_map_package.v1",
        "status": "Draft",
        "createdAt": "2026-09-15T00:00:00Z",
        "mapSpec": { "version": 8, "sources": {}, "layers": [] },
        "view": { "center": [-157.8583, 21.3069], "zoom": 5 },
        "layers": [],
        "widgets": [],
        "controls": [],
        "interactions": []
        """;

    [Theory]
    [InlineData("")]
    [InlineData("\"sourceBindings\": []")]
    [InlineData("\"styleRefs\": [{ \"styleId\": \"status-ramp\", \"label\": \"Status\" }]")]
    [InlineData("\"initialView\": {}")]
    [InlineData("\"initialView\": { \"zoom\": 5 }")]
    [InlineData("\"initialView\": { \"center\": [-157.8583, 21.3069] }")]
    [InlineData("\"initialView\": { \"crs\": \"EPSG:4326\" }")]
    [InlineData("\"initialView\": { \"center\": [-157.8583, 21.3069], \"zoom\": 11.4, \"pitch\": 30, \"bearing\": -12.5 }")]
    [InlineData("\"initialView\": { \"bbox\": [-158.3, 20.9, -157.4, 21.7], \"center\": [-157.8583, 21.3069], \"zoom\": 5, \"crs\": \"EPSG:4326\" }")]
    [InlineData("\"initialView\": null")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"ogc_features\", \"locator\": { \"url\": \"https://example.test/ogc\" } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"ogc_features\", \"locator\": { \"collectionId\": \"parcels\" } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"ogc_features\", \"locator\": { \"collectionId\": 7 } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"geoservices_feature_service\", \"locator\": { \"serviceId\": \"parcels\", \"layerId\": 0 } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"geoservices_map_service\", \"locator\": { \"serviceId\": \"parcels\", \"layerId\": \"0\" } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": { \"typeName\": \"honua:parcels\" } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"odata\", \"locator\": { \"entitySet\": \"Parcels\" } }]")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"imagery\", \"protocol\": \"wmts\", \"locator\": {}, \"metadata\": { \"tier\": \"gold\" } }]")]
    [InlineData("\"legend\": [{ \"label\": \"Open\" }, { \"label\": \"Scale\", \"minValue\": 0, \"maxValue\": 10, \"iconUrl\": \"/icons/scale.png\" }]")]
    [InlineData("\"popupBindings\": [{ \"sourceId\": \"parcels\", \"template\": \"{name}\", \"title\": \"Parcel\" }]")]
    [InlineData("\"labelBindings\": [{ \"sourceId\": \"parcels\", \"fieldName\": \"name\", \"placement\": \"polygon\" }]")]
    [InlineData("\"boundArtifacts\": [\"artifact-1\"], \"attribution\": [{ \"text\": \"Fixture\" }], \"provenance\": { \"generatedBy\": \"honua-cli\" }")]
    public void Validate_SchemaValidMapBody_IsValid(string addedMember)
    {
        var summary = ValidateBody(ReplayBody(addedMember));

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(StudioPackageValidationStatus.Valid, summary.Status);
    }

    [Theory]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"ogc_features\" }]", "studio.map.member.required", "/body/sourceBindings/0/locator")]
    [InlineData("\"sourceBindings\": [{ \"protocol\": \"ogc_features\", \"locator\": {} }]", "studio.map.member.required", "/body/sourceBindings/0/sourceId")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"\", \"protocol\": \"ogc_features\", \"locator\": {} }]", "studio.map.member.empty", "/body/sourceBindings/0/sourceId")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"ftp\", \"locator\": {} }]", "studio.map.member.enum", "/body/sourceBindings/0/protocol")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": \"https://example.test\" }]", "studio.map.member.type", "/body/sourceBindings/0/locator")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": { \"layerId\": true } }]", "studio.map.member.type", "/body/sourceBindings/0/locator/layerId")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": { \"collectionId\": 1.5 } }]", "studio.map.member.type", "/body/sourceBindings/0/locator/collectionId")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": { \"url\": 42 } }]", "studio.map.member.type", "/body/sourceBindings/0/locator/url")]
    [InlineData("\"sourceBindings\": [{ \"sourceId\": \"parcels\", \"protocol\": \"wfs\", \"locator\": {}, \"metadata\": { \"a/b\": 1 } }]", "studio.map.member.type", "/body/sourceBindings/0/metadata/a~1b")]
    [InlineData("\"sourceBindings\": [null]", "studio.map.member.type", "/body/sourceBindings/0")]
    [InlineData("\"sourceBindings\": {}", "studio.map.member.type", "/body/sourceBindings")]
    [InlineData("\"initialView\": []", "studio.map.member.type", "/body/initialView")]
    [InlineData("\"initialView\": { \"bbox\": [1, 2, 3] }", "studio.map.initial-view.bbox.invalid", "/body/initialView/bbox")]
    [InlineData("\"initialView\": { \"bbox\": [3, 2, 1, 4] }", "studio.map.initial-view.bbox.order", "/body/initialView/bbox")]
    [InlineData("\"initialView\": { \"center\": [1, 2, 3] }", "studio.map.initial-view.center.invalid", "/body/initialView/center")]
    [InlineData("\"initialView\": { \"center\": [\"1\", 2] }", "studio.map.initial-view.center.invalid", "/body/initialView/center")]
    [InlineData("\"initialView\": { \"zoom\": \"5\" }", "studio.map.member.type", "/body/initialView/zoom")]
    [InlineData("\"initialView\": { \"crs\": \"WGS84\" }", "studio.map.initial-view.crs.invalid", "/body/initialView/crs")]
    [InlineData("\"styleRefs\": [{ \"styleId\": \"\" }]", "studio.map.member.empty", "/body/styleRefs/0/styleId")]
    [InlineData("\"styleRefs\": [{ \"styleId\": \"ramp\", \"body\": { \"zone-outline\": 3 } }]", "studio.map.member.type", "/body/styleRefs/0/body/zone-outline")]
    [InlineData("\"legend\": [{ \"color\": \"#ffffff\" }]", "studio.map.member.required", "/body/legend/0/label")]
    [InlineData("\"popupBindings\": [{ \"fieldName\": \"name\" }]", "studio.map.member.required", "/body/popupBindings/0/sourceId")]
    [InlineData("\"labelBindings\": [{ \"sourceId\": \"parcels\" }]", "studio.map.member.required", "/body/labelBindings/0/fieldName")]
    [InlineData("\"boundArtifacts\": [1]", "studio.map.member.type", "/body/boundArtifacts/0")]
    [InlineData("\"mapSpec2\": 1, \"templateId\": 5", "studio.map.member.type", "/body/templateId")]
    public void Validate_SchemaInvalidMapMember_NamesTheOffendingPath(string addedMember, string code, string path)
    {
        var summary = ValidateBody(ReplayBody(addedMember));

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        var diagnostic = Assert.Single(summary.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(path, diagnostic.Path);
    }

    [UnitTest]
    public void Validate_MapBodyWithoutIdentity_NamesEachMissingMember()
    {
        var summary = ValidateBody("""{"mapSpec":{"version":8,"sources":{},"layers":[]}}""");

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.DoesNotContain(summary.Diagnostics, d => d.Code == "studio.body.invalid");
        Assert.Contains(summary.Diagnostics, d => d.Code == "studio.map.member.required" && d.Path == "/body/mapPackageId");
        Assert.Contains(summary.Diagnostics, d => d.Code == "studio.map.member.required" && d.Path == "/body/format");
    }

    [UnitTest]
    public void Validate_MapBodyWithoutStatusOrTimestamps_IsValid()
    {
        // The portable schema requires neither; the server record used to require both.
        var summary = ValidateBody("""{"mapPackageId":"map-4898","format":"honua_map_package.v1","sourceBindings":[]}""");

        Assert.Empty(summary.Diagnostics);
    }

    [UnitTest]
    public void Validate_MapBodyWithUnknownStatusBadTimestampOrOtherFormat_NamesEachMember()
    {
        var summary = ValidateBody(
            """{"mapPackageId":"map-4898","format":"honua_map_package.v2","status":"Published","createdAt":"yesterday"}""");

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.Collection(
            summary.Diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal),
            d => Assert.Equal(("studio.map.timestamp.invalid", "/body/createdAt"), (d.Code, d.Path)),
            d => Assert.Equal(("studio.map.format.invalid", "/body/format"), (d.Code, d.Path)),
            d => Assert.Equal(("studio.map.member.enum", "/body/status"), (d.Code, d.Path)));
    }

    [UnitTest]
    public void Validate_SdkCanonicalMapArtifact_IsValid()
    {
        var summary = ValidateBody(MapPackageCanonicalFixture.RuntimeParityShowcase);

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(StudioPackageValidationStatus.Valid, summary.Status);
    }

    [UnitTest]
    public async Task CreateDraft_SdkCanonicalMapArtifact_RoundTripsUnchangedAndValid()
    {
        using var provider = BuildServiceProvider();
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        using var fixture = JsonDocument.Parse(MapPackageCanonicalFixture.RuntimeParityShowcase);

        var created = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            ItemId = Guid.NewGuid(),
            PackageKey = "runtime-parity-showcase",
            WorkspaceId = "studio",
            OwnerId = "author",
            ActorId = "author",
            Envelope = MapEnvelope(fixture.RootElement.Clone()),
        });

        Assert.Equal(StudioPackageValidationStatus.Valid, created.Validation.Status);
        Assert.Empty(created.Validation.Diagnostics);

        var reread = await service.GetDraftAsync(created.DraftId);
        Assert.NotNull(reread);
        Assert.True(JsonElement.DeepEquals(fixture.RootElement, reread!.Envelope.Body!.Value));

        // The stored envelope survives its own wire contract and still validates.
        var wire = JsonSerializer.SerializeToUtf8Bytes(reread.Envelope, StudioJsonContext.Default.StudioPackageEnvelope);
        var decoded = JsonSerializer.Deserialize(wire, StudioJsonContext.Default.StudioPackageEnvelope);
        Assert.NotNull(decoded);
        Assert.True(JsonElement.DeepEquals(fixture.RootElement, decoded!.Body!.Value));
        var revalidated = provider.GetRequiredService<IStudioPackageValidator>().Validate(decoded);
        Assert.Empty(revalidated.Diagnostics);
        Assert.Equal(StudioPackageValidationStatus.Valid, revalidated.Status);
    }

    private static string ReplayBody(string addedMember)
        => addedMember.Length == 0
            ? $$"""{ {{ReplayBaseMembers}} }"""
            : $$"""{ {{ReplayBaseMembers}}, {{addedMember}} }""";

    private static StudioValidationSummary ValidateBody(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        var validator = new StudioPackageValidator(
            new StudioPackageFamilyRegistry(new InMemoryStudioPackageStore()),
            TimeProvider.System);

        return validator.Validate(MapEnvelope(document.RootElement.Clone()));
    }

    private static StudioPackageEnvelope MapEnvelope(JsonElement body) => new()
    {
        Family = StudioPackageFamily.Map,
        SchemaVersion = "1.0",
        Format = "honua_map_package.v1",
        Body = body,
    };

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }
}
