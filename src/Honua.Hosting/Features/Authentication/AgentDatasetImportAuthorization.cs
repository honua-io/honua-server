// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Domain;

namespace Honua.Infrastructure.Authentication;

// A permission ceiling for one logical new-table destination. The canonical task
// still checks its original human-selected source, workspace, turn and credential;
// OAuth and the execution lease remain independent authorization requirements.
internal static class AgentDatasetImportAuthorization
{
    internal const string Operation = "dataset-import";
    private const string PermissionPrefix = "agent:" + Operation + ":";
    private const string ResourcePrefix = Operation + ":";

    internal static string Permission(string targetId, DatasetImportDestination destination)
        => PermissionPrefix + Scope(targetId, destination);

    internal static string ResourceId(string targetId, DatasetImportDestination destination)
        => ResourcePrefix + Scope(targetId, destination);

    internal static bool HasPermission(ClaimsPrincipal principal, string targetId, DatasetImportDestination destination)
        => IsScope(Scope(targetId, destination)) && principal.HasClaim("permission", Permission(targetId, destination));

    internal static bool TryPermissionScope(string permission, out string scope)
    {
        scope = permission.StartsWith(PermissionPrefix, StringComparison.Ordinal) ? permission[PermissionPrefix.Length..] : string.Empty;
        return IsScope(scope);
    }

    internal static bool AllowsOperator(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => AgentCredentialAuthority.IsAgent(principal)
            && request.ResourceType == OperatorResourceType.Catalog
            && request.Operation is OperatorOperation.Create or OperatorOperation.Read
            && request.ResourceId is { } resource && resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            && IsScope(resource[ResourcePrefix.Length..])
            && principal.HasClaim("permission", PermissionPrefix + resource[ResourcePrefix.Length..]);

    internal static bool IsScope(string scope)
        => scope.Split('/') is [var target, var schema, var table]
            && target.Length is > 0 and <= 128
            && target.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && Identifier(schema, underscoreFirst: true) && Identifier(table, underscoreFirst: false);

    private static string Scope(string targetId, DatasetImportDestination destination)
        => targetId + "/" + destination.Schema + "/" + destination.Table;

    private static bool Identifier(string value, bool underscoreFirst)
        => value.Length is > 0 and <= 63
            && (value[0] is >= 'a' and <= 'z' || underscoreFirst && value[0] == '_')
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
