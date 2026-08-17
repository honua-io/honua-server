// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Publishing.Reports;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Publishing.Reports;

/// <summary>
/// Unit tests for the structural chart-spec rules on <see cref="ReportDocumentValidator"/>: a chart panel
/// must carry a non-empty Vega-Lite spec (<c>chartSpecRequired</c>) and, conversely, a non-chart panel must
/// not carry one (<c>chartSpecNotAllowed</c>).
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ReportDocumentValidatorTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_ChartPanelWithSpec_IsValid()
    {
        var result = ReportDocumentValidator.Validate(DocumentWith(new ReportPanel
        {
            Id = "p1",
            Kind = ReportPanelKinds.Chart,
            ChartSpec = Spec("""{"mark":"bar"}"""),
        }));

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Operation(Operations.Query)]
    [Theory]
    [InlineData(ReportPanelKinds.Map)]
    [InlineData(ReportPanelKinds.Table)]
    [InlineData(ReportPanelKinds.Text)]
    public void Validate_NonChartPanelWithChartSpec_IsRejected(string kind)
    {
        var result = ReportDocumentValidator.Validate(DocumentWith(new ReportPanel
        {
            Id = "p1",
            Kind = kind,
            Title = "Stray",
            ChartSpec = Spec("""{"mark":"bar"}"""),
        }));

        result.IsValid.Should().BeFalse();
        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be("chartSpecNotAllowed");
        issue.Severity.Should().Be("error");
        issue.Path.Should().Be("$.panels[0].chartSpec");
        issue.Message.Should().Contain(kind);
    }

    [Operation(Operations.Query)]
    [Theory]
    [InlineData(ReportPanelKinds.Map)]
    [InlineData(ReportPanelKinds.Table)]
    [InlineData(ReportPanelKinds.Text)]
    public void Validate_NonChartPanelWithoutChartSpec_IsValid(string kind)
    {
        var result = ReportDocumentValidator.Validate(DocumentWith(new ReportPanel
        {
            Id = "p1",
            Kind = kind,
        }));

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_NonChartPanelWithEmptyChartSpec_IsValid()
    {
        // An empty (round-tripped) spec is indistinguishable from an absent one, so it must not trip the rule.
        var result = ReportDocumentValidator.Validate(DocumentWith(new ReportPanel
        {
            Id = "p1",
            Kind = ReportPanelKinds.Text,
            ChartSpec = Spec("{}"),
        }));

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Validate_ChartPanelWithoutSpec_ReturnsChartSpecRequired()
    {
        var result = ReportDocumentValidator.Validate(DocumentWith(new ReportPanel
        {
            Id = "p1",
            Kind = ReportPanelKinds.Chart,
        }));

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Code.Should().Be("chartSpecRequired");
    }

    private static ReportDocument DocumentWith(params ReportPanel[] panels) => new()
    {
        Title = "Report",
        Panels = panels,
    };

    private static JsonElement Spec(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
