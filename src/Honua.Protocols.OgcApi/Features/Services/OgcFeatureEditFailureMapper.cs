// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Maps writer <see cref="EditOperationResult"/> failures onto OGC HTTP statuses
/// from the safe client message. Writer error codes stay unchanged.
/// </summary>
internal static class OgcFeatureEditFailureMapper
{
    internal const string ConflictMessage = "The operation conflicted with existing data.";
    internal const string InvalidDataMessage = "Invalid feature data.";
    internal const string GeometryRequiredMessage = "Geometry is required for create operation on a spatial layer";

    internal static IResult ToResult(HttpContext context, EditOperationResult result, string serverDetail)
    {
        var status = DetermineStatus(result);
        var clientDetail = string.IsNullOrWhiteSpace(result.ErrorMessage) ? serverDetail : result.ErrorMessage;
        return status switch
        {
            404 => StandardErrorHelpers.CreateNotFound(context, clientDetail),
            409 => StandardErrorHelpers.CreateConflict(context, clientDetail),
            400 => StandardErrorHelpers.CreateBadRequest(context, clientDetail),
            _ => StandardErrorHelpers.CreateInternalServerError(context, serverDetail)
        };
    }

    internal static int DetermineStatus(EditOperationResult editResult)
    {
        if (string.Equals(editResult.ErrorMessage, "Operation rolled back.", StringComparison.Ordinal))
        {
            return 424;
        }

        if (Contains(editResult.ErrorMessage, "not found"))
        {
            return 404;
        }

        if (string.Equals(editResult.ErrorMessage, ConflictMessage, StringComparison.Ordinal))
        {
            return 409;
        }

        if (string.Equals(editResult.ErrorMessage, InvalidDataMessage, StringComparison.Ordinal) ||
            string.Equals(editResult.ErrorMessage, GeometryRequiredMessage, StringComparison.Ordinal))
        {
            return 400;
        }

        return editResult.ErrorCode >= 500 ? 500 : 400;
    }

    private static bool Contains(string? message, string fragment)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
