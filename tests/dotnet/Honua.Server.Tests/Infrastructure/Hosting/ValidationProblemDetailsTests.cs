// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Validation.Contracts;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure.Hosting;

/// <summary>
/// Pins the wire shape emitted by
/// <see cref="ProblemDetailsHelpers.CreateValidationProblem"/>: an RFC 7807
/// problem document carrying the shared field-level validation contract as an
/// <c>errors[]</c> extension member.
/// </summary>
public sealed class ValidationProblemDetailsTests
{
    [Fact]
    public async Task CreateValidationProblem_EmitsErrorsExtensionAndProblemShape()
    {
        var errors = new List<FieldValidationError>
        {
            FieldValidationError.Create(
                code: "fieldIdDuplicate",
                message: "Field id 'status' is duplicated.",
                severity: ValidationSeverity.Blocker,
                path: "fields.status",
                fieldId: "status"),
            FieldValidationError.Create(
                code: "rangeIncomplete",
                message: "Range max is missing.",
                severity: ValidationSeverity.Warning),
        };

        var (status, json) = await ExecuteAsync(
            ProblemDetailsHelpers.CreateValidationProblem(BuildContext(), StatusCodes.Status400BadRequest, errors));

        status.Should().Be(StatusCodes.Status400BadRequest);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("type").GetString().Should().Be("https://honua.io/problems/validation");
        root.GetProperty("title").GetString().Should().Be("Bad Request");
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("detail").GetString().Should().Be("2 validation errors occurred.");

        var emitted = root.GetProperty("errors");
        emitted.GetArrayLength().Should().Be(2);

        var first = emitted[0];
        first.GetProperty("code").GetString().Should().Be("fieldIdDuplicate");
        first.GetProperty("severity").GetString().Should().Be("blocker");
        first.GetProperty("path").GetString().Should().Be("fields.status");
        first.GetProperty("fieldId").GetString().Should().Be("status");
        first.GetProperty("message").GetString().Should().Be("Field id 'status' is duplicated.");

        var second = emitted[1];
        second.GetProperty("severity").GetString().Should().Be("warning");
        second.TryGetProperty("path", out _).Should().BeFalse("null path is omitted");
        second.TryGetProperty("fieldId", out _).Should().BeFalse("null fieldId is omitted");
    }

    [Fact]
    public async Task CreateValidationProblem_SingleError_UsesSingularDetail()
    {
        var errors = new List<FieldValidationError>
        {
            FieldValidationError.Create("c", "m"),
        };

        var (_, json) = await ExecuteAsync(
            ProblemDetailsHelpers.CreateValidationProblem(BuildContext(), StatusCodes.Status400BadRequest, errors));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("detail").GetString().Should().Be("1 validation error occurred.");
    }

    [Fact]
    public async Task CreateValidationProblem_HonorsExplicitDetail()
    {
        var (_, json) = await ExecuteAsync(
            ProblemDetailsHelpers.CreateValidationProblem(
                BuildContext(),
                StatusCodes.Status400BadRequest,
                [FieldValidationError.Create("c", "m")],
                detail: "Form package is invalid."));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("detail").GetString().Should().Be("Form package is invalid.");
    }

    private static DefaultHttpContext BuildContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Request = { Path = "/api/v1/forms/packages/p1/validate" },
            Response = { Body = new MemoryStream() },
        };
    }

    private static async Task<(int Status, string Json)> ExecuteAsync(IResult result)
    {
        var context = BuildContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, json);
    }
}
