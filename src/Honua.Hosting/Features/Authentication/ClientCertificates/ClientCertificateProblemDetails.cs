// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal sealed record ClientCertificateProblemDetailsResponse
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Detail { get; init; }

    public required string Code { get; init; }

    public string? Instance { get; init; }

    public string? CorrelationId { get; init; }

    public string? Timestamp { get; init; }

    public string? EnvironmentId { get; init; }

    public string? TrustProfileId { get; init; }
}

internal static class ClientCertificateProblemDetails
{
    public static IResult Create(HttpContext context, ClientCertificateValidationResult validationResult)
    {
        var errorCode = validationResult.ErrorCode ?? ClientCertificateValidationErrorCode.MissingCertificate;
        var statusCode = errorCode == ClientCertificateValidationErrorCode.InsufficientRbac
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;
        return Create(context, errorCode, statusCode, validationResult.Detail, validationResult.EnvironmentId, validationResult.ProfileId);
    }

    public static IResult Create(
        HttpContext context,
        ClientCertificateValidationErrorCode errorCode,
        int statusCode,
        string detail,
        string? environmentId = null,
        string? profileId = null)
    {
        var response = new ClientCertificateProblemDetailsResponse
        {
            Type = ClientCertificateErrorCodes.ToProblemType(errorCode),
            Title = ProblemDetailsHelpers.GetTitle(statusCode),
            Status = statusCode,
            Detail = detail,
            Code = ClientCertificateErrorCodes.ToStableCode(errorCode),
            Instance = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            CorrelationId = string.IsNullOrWhiteSpace(context.TraceIdentifier) ? null : context.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            EnvironmentId = environmentId,
            TrustProfileId = profileId,
        };

        return Results.Json(
            response,
            ClientCertificateInfrastructureJsonContext.Default.ClientCertificateProblemDetailsResponse,
            statusCode: statusCode,
            contentType: ProblemDetailsHelpers.ContentType);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ClientCertificateProblemDetailsResponse))]
[JsonSerializable(typeof(ClientCertificateAuditDetails))]
internal sealed partial class ClientCertificateInfrastructureJsonContext : JsonSerializerContext;
