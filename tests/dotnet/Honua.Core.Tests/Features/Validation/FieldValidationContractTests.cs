// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Validation.Contracts;
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

}
