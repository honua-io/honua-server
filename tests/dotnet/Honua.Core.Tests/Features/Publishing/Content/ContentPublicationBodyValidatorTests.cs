// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Validation.Contracts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Publishing.Content;

/// <summary>
/// Unit tests for the publish-time chart-spec rules on <c>ContentPublicationBodyValidator</c>: a chart
/// panel must declare a Vega-Lite spec (<c>publication.panel.chartSpec.vegaLite</c>) and, conversely, a
/// non-chart panel must not carry a chart spec at all
/// (<c>publication.panel.chartSpec.notAllowed</c>, honua-server#3263).
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ContentPublicationBodyValidatorTests
{
    private const string VegaLiteSpec =
        """{"$schema":"https://vega.github.io/schema/vega-lite/v5.json","mark":"bar"}""";

    [Operation(Operations.Create)]
    [Theory]
    // Every non-chart panel kind the report and dashboard documents allow.
    [InlineData("map")]
    [InlineData("table")]
    [InlineData("text")]
    [InlineData("filter")]
    [InlineData("metric")]
    public void Validate_NonChartPanelWithChartSpec_IsRejected(string kind)
    {
        var payload = Payload($$"""{"kind":"{{kind}}","bindingAlias":"parcels","chartSpec":{{VegaLiteSpec}}}""");

        var errors = Capture(ContentPublicationKind.Report, payload);

        var error = errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("publication.panel.chartSpec.notAllowed");
        error.Severity.Should().Be(ValidationSeverity.Error);
        error.Path.Should().Be("/panels/0/chartSpec");
        error.Message.Should().Contain(kind);
    }

    [Operation(Operations.Create)]
    [Theory]
    [InlineData("map")]
    [InlineData("table")]
    [InlineData("text")]
    [InlineData("filter")]
    [InlineData("metric")]
    public void Validate_NonChartPanelWithoutChartSpec_IsAccepted(string kind)
    {
        var payload = Payload($$"""{"kind":"{{kind}}","bindingAlias":"parcels"}""");

        Capture(ContentPublicationKind.Report, payload).Should().BeEmpty();
    }

    [Operation(Operations.Create)]
    [Theory]
    // Absent, explicitly null, an empty object, an empty string, and a whitespace string are all
    // indistinguishable from "no spec", so none of them may trip the rule.
    [InlineData("""{"kind":"map","bindingAlias":"parcels"}""")]
    [InlineData("""{"kind":"map","bindingAlias":"parcels","chartSpec":null}""")]
    [InlineData("""{"kind":"map","bindingAlias":"parcels","chartSpec":{}}""")]
    [InlineData("""{"kind":"map","bindingAlias":"parcels","chartSpec":""}""")]
    [InlineData("""{"kind":"map","bindingAlias":"parcels","chartSpec":"   "}""")]
    public void Validate_NonChartPanelWithEmptyChartSpec_IsAccepted(string panel)
    {
        Capture(ContentPublicationKind.Report, Payload(panel)).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Validate_NonChartPanelWithRawStringChartSpec_IsRejected()
    {
        // A chart spec that did not parse round-trips as a raw string. It is still an authored chart on a
        // panel that will never render one, so the converse rule must catch it too.
        var payload = Payload("""{"kind":"table","bindingAlias":"parcels","chartSpec":"{\"mark\":\"bar\"}"}""");

        Capture(ContentPublicationKind.Report, payload)
            .Should().ContainSingle().Which.Code.Should().Be("publication.panel.chartSpec.notAllowed");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Validate_ChartPanelWithVegaLiteSpec_IsAccepted()
    {
        var payload = Payload($$"""{"kind":"chart","bindingAlias":"parcels","chartSpec":{{VegaLiteSpec}}}""");

        Capture(ContentPublicationKind.Dashboard, payload).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Validate_ChartPanelWithoutSpec_StillReportsVegaLiteRequirement()
    {
        // The pre-existing requirement must be untouched by its converse.
        var payload = Payload("""{"kind":"chart","bindingAlias":"parcels"}""");

        Capture(ContentPublicationKind.Dashboard, payload)
            .Should().ContainSingle().Which.Code.Should().Be("publication.panel.chartSpec.vegaLite");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Validate_MapPublicationWithStrayChartSpec_IsAcceptedOpaquely()
    {
        // Only report/dashboard payloads are inspected; a map payload stays an opaque blob.
        var payload = Payload("""{"kind":"map","bindingAlias":"parcels","chartSpec":{"mark":"bar"}}""");

        Capture(ContentPublicationKind.Map, payload).Should().BeEmpty();
    }

    private static string Payload(string panelJson) =>
        $$"""{"bindings":[{"alias":"parcels","contentRef":"svc.parcels"}],"panels":[{{panelJson}}]}""";

    private static IReadOnlyList<FieldValidationError> Capture(ContentPublicationKind kind, string payload)
    {
        try
        {
            ContentPublicationBodyValidator.Validate(kind, payload);
            return [];
        }
        catch (ContentPublicationValidationException exception)
        {
            return exception.Errors;
        }
    }
}
