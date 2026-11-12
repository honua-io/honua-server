// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Assets;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Assets;

// Shared HTTP plumbing for asset and task commands: request reading, command identity and receipt responses.
internal static class AssetCommandHttp
{
    public const string OutcomeHeader = "Honua-Command-Outcome";

    public static async Task<T?> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ResolveActor(HttpContext context)
        => ServiceDataEditorAuthorization.ResolveOwnerSubject(context.User) ?? AuditEvent.AnonymousActor;

    // The hash covers who sent the command as well as what it asks, so reuse by another actor is a conflict, not a replay.
    public static AssetCommand CreateCommand<T>(HttpContext context, CommandSubject subject, (T Request, JsonTypeInfo<T> TypeInfo) body)
    {
        var actor = ResolveActor(context);
        var canonical = $"{actor}\n{subject.Action}\n{subject.SubjectId:D}\n{JsonSerializer.Serialize(body.Request, body.TypeInfo)}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new AssetCommand(subject.CommandId, subject.ServiceId, subject.Action, actor, hash);
    }

    public static IResult Rejected(HttpContext context, IReadOnlyList<AssetFieldError> errors)
    {
        ApplyNoStore(context.Response);
        return Results.Json(
            new AssetValidationProblem { Errors = errors },
            AssetsJsonContext.Default.AssetValidationProblem,
            statusCode: StatusCodes.Status400BadRequest);
    }

    public static IResult Rejected(HttpContext context, string field, string message)
        => Rejected(context, [new AssetFieldError(field, message)]);

    public static IResult ToResult(HttpContext context, AssetCommandResult result, int acceptedStatusCode)
    {
        ApplyNoStore(context.Response);
        return result.Status switch
        {
            AssetCommandStatus.Accepted => Json(context, result.ResponseJson!, AssetCommandOutcome.Accepted, acceptedStatusCode),
            AssetCommandStatus.Duplicate => Json(context, MarkDuplicate(result.ResponseJson!), AssetCommandOutcome.Duplicate, acceptedStatusCode),
            AssetCommandStatus.VersionConflict when result.ResponseJson is not null
                => Json(context, result.ResponseJson, AssetCommandOutcome.Conflict, StatusCodes.Status409Conflict),
            AssetCommandStatus.VersionConflict or AssetCommandStatus.CommandReused
                => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, result.Reason ?? "The command conflicts with the current state."),
            AssetCommandStatus.Forbidden => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status403Forbidden, result.Reason ?? "The caller may not perform this command."),
            AssetCommandStatus.NotFound => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The resource was not found."),
            _ => Rejected(context, result.Field, result.Reason ?? "The command is not allowed in the current state."),
        };
    }

    public static void ApplyNoStore(HttpResponse response) => response.Headers.CacheControl = "private, no-store";

    private static IResult Json(HttpContext context, string body, string outcome, int statusCode)
    {
        context.Response.Headers[OutcomeHeader] = outcome;
        return Results.Content(body, "application/json", Encoding.UTF8, statusCode);
    }

    // A replay returns the stored receipt; only the outcome marker differs from the first response.
    private static string MarkDuplicate(string storedJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(storedJson)!.AsObject();
        node["outcome"] = AssetCommandOutcome.Duplicate;
        return node.ToJsonString();
    }
}

internal readonly record struct CommandSubject(Guid CommandId, string ServiceId, string Action, Guid SubjectId);
