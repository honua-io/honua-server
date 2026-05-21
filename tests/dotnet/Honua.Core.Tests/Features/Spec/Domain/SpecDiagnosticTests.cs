// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Spec.Domain;

/// <summary>
/// Unit coverage for <see cref="SourceSpan"/> and the <see cref="SpecDiagnostic"/>
/// severity factories. Diagnostics are part of the public spec API contract —
/// IDE clients filter on severity and code, so these helpers must stay stable
/// (#1144).
/// </summary>
public sealed class SpecDiagnosticTests
{
    [UnitTest]
    public void SourceSpan_Synthetic_HasNoLocation()
    {
        SourceSpan.Synthetic.HasLocation.Should().BeFalse();
        SourceSpan.Synthetic.Line.Should().Be(0);
        SourceSpan.Synthetic.Column.Should().Be(0);
        SourceSpan.Synthetic.Offset.Should().Be(0);
        SourceSpan.Synthetic.Length.Should().Be(0);
    }

    [UnitTest]
    public void SourceSpan_RealLocation_ReportsHasLocation()
    {
        var span = new SourceSpan(Line: 5, Column: 12, Offset: 100, Length: 7);

        span.HasLocation.Should().BeTrue();
        span.Line.Should().Be(5);
        span.Column.Should().Be(12);
        span.Offset.Should().Be(100);
        span.Length.Should().Be(7);
    }

    [UnitTest]
    public void SourceSpan_ZeroLineOrColumn_IsConsideredSynthetic()
    {
        new SourceSpan(0, 5, 0, 0).HasLocation.Should().BeFalse();
        new SourceSpan(5, 0, 0, 0).HasLocation.Should().BeFalse();
    }

    [UnitTest]
    public void SourceSpan_EqualityIsValueBased()
    {
        var a = new SourceSpan(1, 1, 0, 4);
        var b = new SourceSpan(1, 1, 0, 4);
        var c = new SourceSpan(1, 1, 0, 5);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [UnitTest]
    public void Error_Factory_ProducesErrorSeverity()
    {
        var span = new SourceSpan(2, 3, 10, 1);

        var diag = SpecDiagnostic.Error(SpecDiagnosticCode.SyntaxError, "boom", span, "/foo");

        diag.Severity.Should().Be(SpecDiagnosticSeverity.Error);
        diag.Code.Should().Be(SpecDiagnosticCode.SyntaxError);
        diag.Message.Should().Be("boom");
        diag.Span.Should().Be(span);
        diag.Path.Should().Be("/foo");
    }

    [UnitTest]
    public void Warning_Factory_ProducesWarningSeverity()
    {
        var diag = SpecDiagnostic.Warning(SpecDiagnosticCode.SyntaxError, "soft", SourceSpan.Synthetic);

        diag.Severity.Should().Be(SpecDiagnosticSeverity.Warning);
        diag.Path.Should().BeNull();
    }

    [UnitTest]
    public void Info_Factory_ProducesInfoSeverity()
    {
        var diag = SpecDiagnostic.Info(SpecDiagnosticCode.SyntaxError, "fyi", SourceSpan.Synthetic);

        diag.Severity.Should().Be(SpecDiagnosticSeverity.Info);
    }

    [UnitTest]
    public void Diagnostic_EqualityIsValueBased()
    {
        var span = new SourceSpan(1, 1, 0, 4);
        var a = SpecDiagnostic.Error(SpecDiagnosticCode.SyntaxError, "m", span);
        var b = SpecDiagnostic.Error(SpecDiagnosticCode.SyntaxError, "m", span);

        a.Should().Be(b);
    }

    [UnitTest]
    public void Severity_Enum_ContainsExpectedMembers()
    {
        var values = Enum.GetValues<SpecDiagnosticSeverity>();

        values.Should().Contain(new[]
        {
            SpecDiagnosticSeverity.Info,
            SpecDiagnosticSeverity.Warning,
            SpecDiagnosticSeverity.Error,
        });
    }

    [UnitTest]
    public void Diagnostic_DefaultPath_IsNull()
    {
        var diag = SpecDiagnostic.Error(SpecDiagnosticCode.SyntaxError, "m", SourceSpan.Synthetic);

        diag.Path.Should().BeNull();
    }
}
