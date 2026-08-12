// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Validation-gate tests for the standard-owned <c>interactions</c>/<c>layout</c>
/// composition blocks (geospatial-mcp ADR-0030). The editor's admission gate covers
/// bindings authored through <c>honua_studio_bind_interaction</c>; these cover the
/// second gate — a document authored WHOLESALE through the draft-update surface, which
/// never passes through the editor.
/// </summary>
public sealed class StudioPackageValidatorInteractionsTests
{
    [UnitTest]
    public void Validate_MapBodyWithNoInteractionsOrLayout_EmitsNoInteractionDiagnostics()
    {
        var summary = ValidateBody(
            """{"format":"honua_map_package.v1","layers":[{"id":"parcels"}]}""");

        Assert.Empty(InteractionCodes(summary));
    }

    [UnitTest]
    public void Validate_WellFormedInteractionsAndLayout_EmitsNoInteractionDiagnostics()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [
                {
                  "id": "select-parcel-filters-chart",
                  "on": { "ref": "layer:parcels", "event": "featureSelect" },
                  "do": { "ref": "widget:area-chart", "verb": "setFilter", "args": { "field": "parcelId", "value": "$event.featureId" } }
                },
                {
                  "id": "chart-row-moves-map",
                  "on": { "ref": "widget:area-chart", "event": "selection" },
                  "do": { "ref": "map", "verb": "setViewport", "args": { "bbox": "$event.bbox" } },
                  "disabled": true
                }
              ],
              "layout": { "grid": { "columns": 12 }, "items": [{ "ref": "widget:area-chart", "x": 0, "y": 0, "w": 6, "h": 4 }] }
            }
            """);

        Assert.Empty(InteractionCodes(summary));
    }

    [Theory]
    [InlineData("featureDoubleClick", "studio.interaction.event.unsupported")]
    [InlineData("", "studio.interaction.event.unsupported")]
    public void Validate_EventOutsideTheClosedSet_Fails(string eventName, string expectedCode)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "layer:parcels", "event": "{{eventName}}" }, "do": { "ref": "widget:area-chart", "verb": "setFilter" } }
              ]
            }
            """);

        AssertInvalidWith(summary, expectedCode);
    }

    [UnitTest]
    public void Validate_VerbOutsideTheClosedSet_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "layer:parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setBasemap" } }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.interaction.verb.unsupported");
    }

    [UnitTest]
    public void Validate_MalformedRef_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setViewport" } }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.interaction.ref.invalid");
    }

    [UnitTest]
    public void Validate_RefThatDoesNotResolveInTheDocument_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "layer:zoning", "event": "featureSelect" }, "do": { "ref": "widget:not-here", "verb": "setFilter" } }
              ]
            }
            """);

        var diagnostics = summary.Diagnostics
            .Where(d => d.Code == "studio.interaction.ref.unresolved")
            .ToArray();

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.Path == "/body/interactions/0/on/ref");
        Assert.Contains(diagnostics, d => d.Path == "/body/interactions/0/do/ref");
    }

    [UnitTest]
    public void Validate_ControlRef_FailsWithTheControlsUnsupportedMessage()
    {
        // ADR-0030 admits `control:{id}` in the grammar, but Studio composition documents
        // declare no controls collection — so a control ref is a validation FAILURE here,
        // with a message that says why rather than a generic "does not resolve".
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "control:year-slider", "event": "change" }, "do": { "ref": "layer:parcels", "verb": "setFilter" } }
              ]
            }
            """);

        var diagnostic = Assert.Single(
            summary.Diagnostics, d => d.Code == "studio.interaction.ref.control-unsupported");
        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.Equal("/body/interactions/0/on/ref", diagnostic.Path);
        Assert.Contains("declare no controls collection", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("control:year-slider", diagnostic.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Validate_DuplicateInteractionIds_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "layer:parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setViewport" } },
                { "id": "i1", "on": { "ref": "layer:parcels", "event": "featureHover" }, "do": { "ref": "map", "verb": "setViewport" } }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.interaction.id.duplicate");
    }

    [UnitTest]
    public void Validate_InteractionIdOverTheAdvertisedLimit_Fails()
    {
        var id = new string('i', StudioInteractionVocabulary.MaxInteractionIdLength + 1);
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "{{id}}", "on": { "ref": "layer:parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setViewport" } }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.interaction.id.too-long");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Validate_ActionArgsThatAreNotAnObject_Fails(string argsJson)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "layer:parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setViewport", "args": {{argsJson}} } }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.interaction.args.object");
    }

    [UnitTest]
    public void Validate_FanOutOverTheCap_Fails_ButExactlyAtTheCapPasses()
    {
        var atCap = ValidateBody(BodyWithFanOut(StudioInteractionVocabulary.MaxInteractionsPerEventSource));
        Assert.Empty(InteractionCodes(atCap));

        var overCap = ValidateBody(BodyWithFanOut(StudioInteractionVocabulary.MaxInteractionsPerEventSource + 1));
        var diagnostic = Assert.Single(overCap.Diagnostics, d => d.Code == "studio.interactions.fan-out");
        Assert.Equal(StudioPackageValidationStatus.Invalid, overCap.Status);
        Assert.Contains("layer:parcels", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("featureSelect", diagnostic.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Validate_InteractionsThatAreNotAnArray_Fails()
    {
        var summary = ValidateBody(
            """{"format":"honua_map_package.v1","layers":[],"interactions":{"id":"i1"}}""");

        AssertInvalidWith(summary, "studio.interactions.array");
    }

    [Theory]
    [InlineData("interactions", "studio.interactions.array")]
    [InlineData("layout", "studio.layout.object")]
    public void Validate_ExplicitNullCompositionBlock_Fails(string memberName, string expectedCode)
    {
        var summary = ValidateBody(
            $$"""{"format":"honua_map_package.v1","layers":[],"{{memberName}}":null}""");

        AssertInvalidWith(summary, expectedCode);
    }

    [Theory]
    [InlineData("\"y\":0")]
    [InlineData("\"x\":0")]
    public void Validate_LayoutItemMissingEitherOriginCoordinate_Fails(string coordinateJson)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "items": [{ "ref": "widget:area-chart", {{coordinateJson}}, "w": 6, "h": 4 }] }
            }
            """);

        AssertInvalidWith(summary, "studio.layout.item.origin.required");
    }

    [UnitTest]
    public void Validate_UnknownMembersInsideStandardOwnedBlocks_FailAtExactPaths()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [{
                "id": "i1",
                "on": { "ref": "layer:parcels", "event": "featureSelect", "typoEvent": true },
                "do": { "ref": "widget:area-chart", "verb": "setFilter", "typoAction": true },
                "typoInteraction": true
              }],
              "layout": {
                "grid": { "columns": 12, "typoGrid": true },
                "items": [{ "ref": "widget:area-chart", "x": 0, "y": 0, "w": 6, "h": 4, "typoItem": true }],
                "typoLayout": true
              }
            }
            """);

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/interactions/0/typoInteraction");
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/interactions/0/on/typoEvent");
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/interactions/0/do/typoAction");
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/layout/typoLayout");
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/layout/grid/typoGrid");
        Assert.Contains(summary.Diagnostics, d => d.Path == "/body/layout/items/0/typoItem");
    }

    [Theory]
    [InlineData("\"args\":null", "studio.interaction.args.object")]
    [InlineData("\"disabled\":null", "studio.interaction.disabled.boolean")]
    public void Validate_NullTypedInteractionMember_Fails(string memberJson, string expectedCode)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [{
                "id": "i1",
                "on": { "ref": "layer:parcels", "event": "featureSelect" },
                "do": { "ref": "map", "verb": "setViewport"{{(memberJson.StartsWith("\"args", StringComparison.Ordinal) ? ", " + memberJson : string.Empty)}} },
                {{(memberJson.StartsWith("\"disabled", StringComparison.Ordinal) ? memberJson : "\"disabled\":false")}}
              }]
            }
            """);

        AssertInvalidWith(summary, expectedCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-3)]
    public void Validate_GridColumnsOutsideOneToTwentyFour_Fails(int columns)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "grid": { "columns": {{columns}} }, "items": [] }
            }
            """);

        AssertInvalidWith(summary, "studio.layout.grid.columns.invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(24)]
    public void Validate_GridColumnsWithinRange_Passes(int columns)
    {
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "grid": { "columns": {{columns}} } }
            }
            """);

        Assert.Empty(InteractionCodes(summary));
    }

    [UnitTest]
    public void Validate_LayoutItemRefThatDoesNotResolve_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "items": [{ "ref": "widget:not-here", "x": 0, "y": 0, "w": 6, "h": 4 }] }
            }
            """);

        var diagnostic = Assert.Single(summary.Diagnostics, d => d.Code == "studio.layout.item.ref.unresolved");
        Assert.Equal("/body/layout/items/0/ref", diagnostic.Path);
    }

    [UnitTest]
    public void Validate_LayoutItemWithNegativeOriginOrZeroSize_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "items": [{ "ref": "widget:area-chart", "x": -1, "y": 0, "w": 0, "h": 4 }] }
            }
            """);

        Assert.Contains(summary.Diagnostics, d => d.Code == "studio.layout.item.origin.invalid");
        Assert.Contains(summary.Diagnostics, d => d.Code == "studio.layout.item.size.invalid");
    }

    [UnitTest]
    public void Validate_AppFamilyBodyIsGatedToo()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_app_package.v1",
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [
                { "id": "i1", "on": { "ref": "widget:area-chart", "event": "selection" }, "do": { "ref": "layer:missing", "verb": "setFilter" } }
              ]
            }
            """,
            StudioPackageFamily.App);

        AssertInvalidWith(summary, "studio.interaction.ref.unresolved");
    }

    [UnitTest]
    public void Validate_NonCompositionFamily_IsNotGated()
    {
        // Only map/app documents carry composition blocks; a query package that happens to
        // have an `interactions` member is not an ADR-0030 composition document.
        var summary = ValidateBody(
            """{"interactions":[{"id":"i1","on":{"ref":"layer:nope","event":"nope"},"do":{"ref":"map","verb":"nope"}}]}""",
            StudioPackageFamily.Query);

        Assert.Empty(InteractionCodes(summary));
    }

    private static string BodyWithFanOut(int count)
    {
        var bindings = string.Join(
            ",\n    ",
            Enumerable.Range(0, count).Select(i =>
                $$"""{ "id": "i{{i}}", "on": { "ref": "layer:parcels", "event": "featureSelect" }, "do": { "ref": "map", "verb": "setViewport" } }"""));

        return $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "interactions": [
                {{bindings}}
              ]
            }
            """;
    }

    private static string[] InteractionCodes(StudioValidationSummary summary) => summary.Diagnostics
        .Where(d => d.Code.StartsWith("studio.interaction", StringComparison.Ordinal)
            || d.Code.StartsWith("studio.layout", StringComparison.Ordinal)
            || d.Code == "studio.composition.invalid")
        .Select(d => d.Code)
        .ToArray();

    private static void AssertInvalidWith(StudioValidationSummary summary, string code)
    {
        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.Contains(summary.Diagnostics, d => d.Code == code);
    }

    private static StudioValidationSummary ValidateBody(
        string bodyJson,
        StudioPackageFamily family = StudioPackageFamily.Map)
    {
        using var document = JsonDocument.Parse(bodyJson);
        var validator = new StudioPackageValidator(
            new StudioPackageFamilyRegistry(new InMemoryStudioPackageStore()),
            TimeProvider.System);

        return validator.Validate(new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = "1.0",
            Format = FormatFor(family),
            Body = document.RootElement.Clone(),
        });
    }

    private static string FormatFor(StudioPackageFamily family) => family switch
    {
        StudioPackageFamily.Map => "honua_map_package.v1",
        StudioPackageFamily.App => "honua_app_package.v1",
        _ => "studio_query_package.v1",
    };
}
