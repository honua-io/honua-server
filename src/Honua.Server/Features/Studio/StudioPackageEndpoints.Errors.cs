// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio;

internal sealed record StudioErrorMappingContext
{
    public required HttpContext Context { get; init; }
    public required ILogger Logger { get; init; }
    public required string Operation { get; init; }
    public string? ServerErrorDetail { get; init; }
}

internal static partial class StudioPackageEndpoints
{
    private static StudioErrorMappingContext StudioOperationError(
        HttpContext context,
        ILogger logger,
        string operation) => new()
        {
            Context = context,
            Logger = logger,
            Operation = operation
        };

    internal static IResult MapStudioArgumentException(
        StudioErrorMappingContext errorContext,
        ArgumentException ex)
    {
        if (ex is ArgumentNullException)
        {
            StudioEndpointsLog.EndpointFailed(errorContext.Logger, errorContext.Operation, ex);
            return ServerError(errorContext.Context, "The Studio package operation could not be completed.");
        }

        if (ex.Message.StartsWith("Publication intent is invalid", StringComparison.Ordinal))
        {
            return BadRequest(errorContext.Context, ex.Message);
        }

        StudioEndpointsLog.EndpointFailed(errorContext.Logger, errorContext.Operation, ex);
        return BadRequest(errorContext.Context, "The Studio package request is invalid.");
    }

    internal static IResult MapStudioInvalidOperationException(
        StudioErrorMappingContext errorContext,
        InvalidOperationException ex)
    {
        if (ResolveKnownStudioConflictDetail(ex) is { } conflictDetail)
        {
            return Conflict(errorContext.Context, conflictDetail);
        }

        StudioEndpointsLog.EndpointFailed(errorContext.Logger, errorContext.Operation, ex);
        return ServerError(
            errorContext.Context,
            errorContext.ServerErrorDetail ?? "The Studio package operation could not be completed.");
    }

    private static string? ResolveKnownStudioConflictDetail(InvalidOperationException ex)
    {
        var message = ex.Message;
        if (string.Equals(message, "Stale draft generation; refresh and retry.", StringComparison.Ordinal)
            || (message.Contains("stale", StringComparison.OrdinalIgnoreCase)
                && message.Contains("generation", StringComparison.OrdinalIgnoreCase)))
        {
            return "Stale draft generation; refresh and retry.";
        }

        if (message.Contains("idempotency", StringComparison.OrdinalIgnoreCase))
        {
            return "Idempotency-Key was already used with a different request.";
        }

        if (message.Contains("bridged", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("native draft", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot be published", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot be rolled back", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return "The Studio package operation conflicts with the current resource state.";
        }

        return null;
    }

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status400BadRequest, "Bad Request", detail);

    private static IResult Forbidden(HttpContext context, string detail, string code)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status403Forbidden, "Forbidden", detail, code);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status404NotFound, "Not Found", detail);

    private static IResult NotFound(HttpContext context, string detail, string code)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status404NotFound, "Not Found", detail, code);

    private static IResult Conflict(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status409Conflict, "Conflict", detail);

    private static IResult CapabilityUnavailable(HttpContext context, CapabilityUnavailableException exception)
        => ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
            context,
            exception.Message,
            exception.MissingDependency,
            exception.Remediation,
            exception.RemediationRef,
            exception.Capability);

    private static IResult ServerError(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status500InternalServerError, "Internal Server Error", detail);

    private static IResult ServiceUnavailable(HttpContext context, string detail, string code)
        => ProblemDetailsHelpers.CreateProblem(context, ProblemType, StatusCodes.Status503ServiceUnavailable, "Service Unavailable", detail, code);
}
