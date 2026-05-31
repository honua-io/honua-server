// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Validation.Contracts;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Validation;

[Protocol(ProtocolNames.Admin)]
public sealed class FieldValidationContractTests
{
    [UnitTest]
    public void FieldValidationError_SerializesToCanonicalShape()
    {
        var error = FieldValidationError.Create(
            code: "studio.map.initial-view.bbox.order",
            message: "minX must be <= maxX.",
            severity: ValidationSeverity.Blocker,
            path: "/map/initialView/bbox",
            fieldId: "InitialExtent");

        var json = JsonSerializer.Serialize(error, ValidationContractJsonContext.Default.FieldValidationError);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("code").GetString().Should().Be("studio.map.initial-view.bbox.order");
        root.GetProperty("severity").GetString().Should().Be("blocker");
        root.GetProperty("path").GetString().Should().Be("/map/initialView/bbox");
        root.GetProperty("message").GetString().Should().Be("minX must be <= maxX.");
        root.GetProperty("fieldId").GetString().Should().Be("InitialExtent");
    }

    [UnitTest]
    public void FieldValidationError_OmitsNullPathAndFieldId()
    {
        var error = FieldValidationError.Create("generic.code", "message");

        var json = JsonSerializer.Serialize(error, ValidationContractJsonContext.Default.FieldValidationError);

        json.Should().NotContain("path");
        json.Should().NotContain("fieldId");
        json.Should().Contain("\"severity\":\"error\"");
    }

    [UnitTest]
    public void FieldValidationResult_IsValid_TrueWhenOnlyInfoOrWarning()
    {
        var result = FieldValidationResult.FromErrors(new[]
        {
            FieldValidationError.Create("a", "info", ValidationSeverity.Info),
            FieldValidationError.Create("b", "warn", ValidationSeverity.Warning),
        });

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void FieldValidationResult_IsValid_FalseWhenErrorOrBlocker()
    {
        FieldValidationResult.FromErrors(new[] { FieldValidationError.Create("a", "err", ValidationSeverity.Error) })
            .IsValid.Should().BeFalse();
        FieldValidationResult.FromErrors(new[] { FieldValidationError.Create("a", "block", ValidationSeverity.Blocker) })
            .IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void FieldValidationResult_FromNull_IsValidEmpty()
    {
        var result = FieldValidationResult.FromErrors(null);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    public void StudioDiagnostic_NormalizesPreservingAllFields()
    {
        var diagnostic = new StudioValidationDiagnostic
        {
            Code = "studio.binding.key.duplicate",
            Severity = StudioPackageDiagnosticSeverity.Blocker,
            Path = "/bindings/0/key",
            Message = "Duplicate key.",
        };

        var error = diagnostic.ToFieldValidationError();

        error.Code.Should().Be("studio.binding.key.duplicate");
        error.Severity.Should().Be(ValidationSeverity.Blocker);
        error.Path.Should().Be("/bindings/0/key");
        error.Message.Should().Be("Duplicate key.");
        error.FieldId.Should().BeNull();
    }

    [UnitTest]
    public void FormIssue_NormalizesPreservingCodeSeverityFieldMessagePath()
    {
        var issue = new FormValidationIssue
        {
            Code = "fieldIdDuplicate",
            Severity = "warning",
            FieldId = "status",
            Path = "fields.status",
            Message = "Field id 'status' is duplicated.",
        };

        var error = issue.ToFieldValidationError();

        error.Code.Should().Be("fieldIdDuplicate");
        error.Severity.Should().Be(ValidationSeverity.Warning);
        error.FieldId.Should().Be("status");
        error.Path.Should().Be("fields.status");
        error.Message.Should().Be("Field id 'status' is duplicated.");
    }

    [UnitTest]
    public void FormIssue_UnknownSeverity_DefaultsToError()
    {
        var issue = new FormValidationIssue { Code = "c", Severity = "weird", Message = "m" };

        issue.ToFieldValidationError().Severity.Should().Be(ValidationSeverity.Error);
    }

    [UnitTest]
    public void WorkflowFailure_NormalizesFieldPathOntoPathAsError()
    {
        var failure = new WorkflowPackageValidationFailure
        {
            Code = "workflow.graph.cycle",
            Message = "Graph has a cycle.",
            FieldPath = "nodes[2]",
        };

        var error = failure.ToFieldValidationError();

        error.Code.Should().Be("workflow.graph.cycle");
        error.Severity.Should().Be(ValidationSeverity.Error);
        error.Path.Should().Be("nodes[2]");
        error.Message.Should().Be("Graph has a cycle.");
    }

    [UnitTest]
    public void FormResult_RoundTripsAllIssues()
    {
        var formResult = new FormPackageValidationResult
        {
            IsValid = false,
            Issues =
            [
                new FormValidationIssue { Code = "a", Severity = "error", FieldId = "f1", Path = "p1", Message = "m1" },
                new FormValidationIssue { Code = "b", Severity = "warning", FieldId = "f2", Path = "p2", Message = "m2" },
            ],
        };

        var normalized = formResult.ToFieldValidationResult();

        normalized.Errors.Should().HaveCount(2);
        normalized.Errors[0].Code.Should().Be("a");
        normalized.Errors[0].FieldId.Should().Be("f1");
        normalized.Errors[1].Severity.Should().Be(ValidationSeverity.Warning);
        normalized.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void StudioSummary_RoundTripsDiagnostics()
    {
        var summary = new StudioValidationSummary
        {
            Status = StudioPackageValidationStatus.Invalid,
            Diagnostics =
            [
                new StudioValidationDiagnostic
                {
                    Code = "studio.visibility.enum",
                    Severity = StudioPackageDiagnosticSeverity.Error,
                    Path = "/publicationIntent/visibility",
                    Message = "Unsupported visibility.",
                },
            ],
        };

        var normalized = summary.ToFieldValidationResult();

        normalized.Errors.Should().ContainSingle();
        normalized.Errors[0].Code.Should().Be("studio.visibility.enum");
        normalized.Errors[0].Path.Should().Be("/publicationIntent/visibility");
    }

    [UnitTest]
    public void WorkflowResult_SurfacesFailuresAndWarnings()
    {
        var result = WorkflowPackageValidationResult.Failed(
            failures: [new WorkflowPackageValidationFailure { Code = "c", Message = "m", FieldPath = "fp" }],
            warnings: ["deprecated node kind"]);

        var normalized = result.ToFieldValidationResult();

        normalized.Errors.Should().HaveCount(2);
        normalized.Errors[0].Severity.Should().Be(ValidationSeverity.Error);
        normalized.Errors[1].Code.Should().Be("workflow.validation.warning");
        normalized.Errors[1].Severity.Should().Be(ValidationSeverity.Warning);
        normalized.Errors[1].Message.Should().Be("deprecated node kind");
    }
}
