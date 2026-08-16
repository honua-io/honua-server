// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Validation-gate tests for the standard-owned <c>controls</c> composition collection
/// (geospatial-mcp ADR-0031). <see cref="StudioCompositionControlsTests"/> covers the
/// editor's admission gate (controls added through <c>honua_studio_add_control</c>);
/// these cover the second gate — a document authored WHOLESALE through the draft-update
/// surface, which never passes through the editor.
/// </summary>
public sealed class StudioPackageValidatorControlsTests
{
    [UnitTest]
    public void Validate_MapBodyWithNoControls_EmitsNoControlDiagnostics()
    {
        var summary = ValidateBody("""{"format":"honua_map_package.v1","layers":[{"id":"parcels"}]}""");

        Assert.Empty(ControlCodes(summary));
    }

    [UnitTest]
    public void Validate_WellFormedControls_EmitsNoControlDiagnostics()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "controls": [
                { "id": "year-slider", "kind": "timeSlider", "title": "Year built", "sourceId": "parcels", "config": { "field": "yearBuilt" } },
                { "id": "nav", "kind": "navigation" }
              ]
            }
            """);

        Assert.Empty(ControlCodes(summary));
    }

    [UnitTest]
    public void Validate_ControlsThatAreNotAnArray_Fails()
    {
        var summary = ValidateBody(
            """{"format":"honua_map_package.v1","layers":[],"controls":{"id":"nav","kind":"navigation"}}""");

        AssertInvalidWith(summary, "studio.controls.array");
    }

    [UnitTest]
    public void Validate_ControlWithUnknownMember_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "nav", "kind": "navigation", "position": "top-right" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.member.unknown");
    }

    [UnitTest]
    public void Validate_ControlWithoutIdOrKind_Fails()
    {
        AssertInvalidWith(
            ValidateBody("""{"format":"honua_map_package.v1","layers":[],"controls":[{"kind":"navigation"}]}"""),
            "studio.control.id.required");
        AssertInvalidWith(
            ValidateBody("""{"format":"honua_map_package.v1","layers":[],"controls":[{"id":"nav"}]}"""),
            "studio.control.kind.required");
    }

    [UnitTest]
    public void Validate_ControlKindOutsideTheClosedVocabulary_Fails()
    {
        // `draw` is the case that matters: ADR-0031 admits no feature-editing control, so
        // this document gate is the one that stops a wholesale draft-update from smuggling
        // one past the tool-admission gate.
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "sketch", "kind": "draw" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.kind.unsupported");
        var diagnostic = Assert.Single(summary.Diagnostics, d => d.Code == "studio.control.kind.unsupported");
        Assert.Equal("/body/controls/0/kind", diagnostic.Path);
    }

    [UnitTest]
    public void Validate_DuplicateControlIds_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [
                { "id": "year-slider", "kind": "timeSlider" },
                { "id": "year-slider", "kind": "filterSlider" }
              ]
            }
            """);

        AssertInvalidWith(summary, "studio.control.id.duplicate");
    }

    [UnitTest]
    public void Validate_OversizedControlId_Fails()
    {
        var id = new string('c', StudioInteractionVocabulary.MaxControlIdLength + 1);
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "{{id}}", "kind": "navigation" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.id.too-long");
    }

    [UnitTest]
    public void Validate_OversizedControlSourceId_Fails()
    {
        var sourceId = new string('s', StudioInteractionVocabulary.MaxControlSourceIdLength + 1);
        var summary = ValidateBody(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "{{sourceId}}" }],
              "controls": [{ "id": "filter", "kind": "filterSelect", "sourceId": "{{sourceId}}" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.source.too-long");
    }

    [UnitTest]
    public void Validate_ControlConfigThatIsNotAnObject_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "nav", "kind": "navigation", "config": "top-right" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.config.object");
    }

    [UnitTest]
    public void Validate_NullControlTitle_Fails()
    {
        // A whole-envelope update must not slip `null` past the wire-shape gate: the published
        // control schema permits only strings when `title` is present, and deserialization would
        // otherwise treat the null as an omission and mark the draft valid.
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "nav", "kind": "navigation", "title": null }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.title.string");
    }

    [UnitTest]
    public void Validate_NullControlSourceId_Fails()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "filter", "kind": "filterSelect", "sourceId": null }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.sourceId.string");
    }

    [UnitTest]
    public void Validate_NullControlEntry_Fails()
    {
        var summary = ValidateBody("""{"format":"honua_map_package.v1","layers":[],"controls":[null]}""");

        Assert.Equal(StudioPackageValidationStatus.Invalid, summary.Status);
        Assert.NotEmpty(ControlCodes(summary));
    }

    [UnitTest]
    public void Validate_ControlSourceIdThatResolvesToNothing_Fails()
    {
        // The second gate for ADR-0031 source resolution: a body authored wholesale through
        // draft-update never passes the editor, so the document gate has to catch it too.
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "controls": [{ "id": "f", "kind": "filterSelect", "sourceId": "parcel" }]
            }
            """);

        AssertInvalidWith(summary, "studio.control.source.unresolved");
        var diagnostic = Assert.Single(summary.Diagnostics, d => d.Code == "studio.control.source.unresolved");
        Assert.Equal("/body/controls/0/sourceId", diagnostic.Path);
    }

    [UnitTest]
    public void Validate_ControlSourceIdMatchingALayerIdOrItsDatasource_Passes()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels", "sourceId": "ds-parcels" }],
              "controls": [
                { "id": "a", "kind": "filterSelect", "sourceId": "parcels" },
                { "id": "b", "kind": "timeSlider", "sourceId": "ds-parcels" },
                { "id": "nav", "kind": "navigation" }
              ]
            }
            """);

        Assert.Empty(ControlCodes(summary));
    }

    [UnitTest]
    public void Validate_LayoutItemReferencingAControl_Fails()
    {
        // Controls are chrome, not grid items (ADR-0031) — the control ref resolves, but the
        // layout reference space still refuses it, with a message that says why.
        var summary = ValidateBody(
            """
            {
              "format": "honua_map_package.v1",
              "layers": [],
              "controls": [{ "id": "year-slider", "kind": "timeSlider" }],
              "layout": { "items": [{ "ref": "control:year-slider", "x": 0, "y": 0, "w": 3, "h": 2 }] }
            }
            """);

        AssertInvalidWith(summary, "studio.layout.item.ref.control");
        var diagnostic = Assert.Single(summary.Diagnostics, d => d.Code == "studio.layout.item.ref.control");
        Assert.Contains("not a grid item", diagnostic.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void Validate_AppBodyWithControls_IsGatedTheSameWayAsMap()
    {
        var summary = ValidateBody(
            """
            {
              "format": "honua_app_package.v1",
              "layers": [],
              "controls": [{ "id": "sketch", "kind": "draw" }]
            }
            """,
            StudioPackageFamily.App);

        AssertInvalidWith(summary, "studio.control.kind.unsupported");
    }

    private static string[] ControlCodes(StudioValidationSummary summary) => summary.Diagnostics
        .Where(d => d.Code.StartsWith("studio.control", StringComparison.Ordinal)
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
            Format = family == StudioPackageFamily.App ? "honua_app_package.v1" : "honua_map_package.v1",
            Body = document.RootElement.Clone(),
        });
    }
}
