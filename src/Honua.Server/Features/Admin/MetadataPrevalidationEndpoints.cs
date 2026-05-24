// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for Metadata v2 release-package compatibility prevalidation.
/// </summary>
internal static class MetadataPrevalidationEndpoints
{
    /// <summary>
    /// Maps Metadata v2 compatibility prevalidation endpoints.
    /// </summary>
    public static void MapMetadataPrevalidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Validation")
            .RequireAdminAuthorization();

        group.MapPost("/prevalidate", HandlePrevalidate)
            .WithDisplayName("Prevalidate Metadata Release Package Compatibility")
            .WithSummary("Returns an environment-scoped Metadata v2 compatibility report for a release package.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Accepts<MetadataPrevalidateRequest>("application/json")
            .Produces<ApiResponse<MetadataCompatibilityReport>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandlePrevalidate(
        [FromServices] IMetadataCompatibilityPrevalidationService prevalidationService,
        HttpContext context)
    {
        try
        {
            var request = await ReadRequestAsync(context).ConfigureAwait(false);
            var report = await prevalidationService.PrevalidateAsync(
                    request.ToCoreRequest(),
                    context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(
                ApiResponse<MetadataCompatibilityReport>.CreateSuccess(report),
                MetadataPrevalidationJsonContext.Default.ApiResponseMetadataCompatibilityReport);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (JsonException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Request body must be valid JSON.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Metadata compatibility prevalidation could not be completed.");
        }
    }

    private static async ValueTask<MetadataPrevalidateRequest> ReadRequestAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Request body must be a JSON object.");
        }

        ValidateDataScriptCollections(document.RootElement);

        return document.RootElement.Deserialize(MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest)
            ?? throw new ArgumentException("Request body is required.");
    }

    private static void ValidateDataScriptCollections(JsonElement root)
    {
        if (!root.TryGetProperty("dataScripts", out var dataScripts))
        {
            return;
        }

        if (dataScripts.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("DataScripts must be an array.");
        }

        foreach (var script in dataScripts.EnumerateArray())
        {
            if (script.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentException("DataScripts cannot contain null entries.");
            }

            if (script.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var scriptId = ReadScriptId(script);
            ValidateOptionalArray(script, "declaredOperations", scriptId, "declaredOperations");
            ValidateContractCollections(script, "beforeContract", scriptId);
            ValidateContractCollections(script, "afterContract", scriptId);
        }
    }

    private static void ValidateContractCollections(JsonElement script, string propertyName, string scriptId)
    {
        if (!script.TryGetProperty(propertyName, out var contract) ||
            contract.ValueKind == JsonValueKind.Null ||
            contract.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!ValidateOptionalArray(contract, "resources", scriptId, $"{propertyName}.resources", out var resources))
        {
            return;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentException($"Data script '{scriptId}' {propertyName}.resources cannot contain null entries.");
            }

            if (resource.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ValidateOptionalArray(resource, "requiredIdentifiers", scriptId, $"{propertyName}.resources.requiredIdentifiers");
            ValidateOptionalArray(resource, "domains", scriptId, $"{propertyName}.resources.domains");
            ValidateOptionalArray(resource, "indexes", scriptId, $"{propertyName}.resources.indexes");
            ValidateOptionalArray(resource, "capabilities", scriptId, $"{propertyName}.resources.capabilities");
            ValidateOptionalArray(resource, "supportedFormats", scriptId, $"{propertyName}.resources.supportedFormats");

            if (ValidateOptionalArray(resource, "fields", scriptId, $"{propertyName}.resources.fields", out var fields))
            {
                ValidateFieldCollections(fields, scriptId, propertyName);
            }

            if (resource.TryGetProperty("storage", out var storage) &&
                storage.ValueKind == JsonValueKind.Object)
            {
                ValidateOptionalArray(storage, "capabilities", scriptId, $"{propertyName}.resources.storage.capabilities");
            }
        }
    }

    private static void ValidateFieldCollections(JsonElement fields, string scriptId, string propertyName)
    {
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentException($"Data script '{scriptId}' {propertyName}.resources.fields cannot contain null entries.");
            }

            if (field.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ValidateOptionalArray(field, "semanticRoles", scriptId, $"{propertyName}.resources.fields.semanticRoles");
            ValidateOptionalArray(field, "domains", scriptId, $"{propertyName}.resources.fields.domains");
            ValidateOptionalArray(field, "indexes", scriptId, $"{propertyName}.resources.fields.indexes");
        }
    }

    private static bool ValidateOptionalArray(
        JsonElement owner,
        string propertyName,
        string scriptId,
        string collectionPath)
        => ValidateOptionalArray(owner, propertyName, scriptId, collectionPath, out _);

    private static bool ValidateOptionalArray(
        JsonElement owner,
        string propertyName,
        string scriptId,
        string collectionPath,
        out JsonElement array)
    {
        array = default;
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Data script '{scriptId}' {collectionPath} must be an array.");
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
            {
                throw new ArgumentException($"Data script '{scriptId}' {collectionPath} cannot contain null entries.");
            }
        }

        array = value;
        return true;
    }

    private static string ReadScriptId(JsonElement script)
    {
        if (script.TryGetProperty("scriptId", out var scriptId) &&
            scriptId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(scriptId.GetString()))
        {
            return scriptId.GetString()!;
        }

        return "<unknown>";
    }
}
