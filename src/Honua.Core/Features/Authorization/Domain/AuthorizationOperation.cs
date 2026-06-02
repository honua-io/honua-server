// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// The canonical per-operation taxonomy for RBAC authorization decisions (#1375).
/// A <see cref="PermissionGrant.Operation"/> string is matched against these
/// values (case-insensitively) by <c>IPermissionResolver</c>. Grants are
/// expressed with the wire string form
/// (<see cref="AuthorizationOperationExtensions.ToWireName"/>); the
/// wildcard <c>"*"</c> grant matches every operation.
/// </summary>
/// <remarks>
/// The taxonomy intentionally goes beyond the coarse read-vs-write seam of
/// <c>AccessPolicy</c>. <see cref="Query"/> is the canonical read operation and
/// <see cref="Read"/> is retained as a synonym so legacy <c>"read"</c> grants
/// (seeded by the original <c>InMemoryRoleStore</c>) keep working.
/// </remarks>
public enum AuthorizationOperation
{
    /// <summary>
    /// Read a feature/record set (the canonical read operation; wire name <c>"query"</c>).
    /// </summary>
    Query = 0,

    /// <summary>
    /// Insert/create new features (wire name <c>"insert"</c>).
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Update existing features (wire name <c>"update"</c>).
    /// </summary>
    Update = 2,

    /// <summary>
    /// Delete features (wire name <c>"delete"</c>).
    /// </summary>
    Delete = 3,

    /// <summary>
    /// Bulk/streaming export of data (wire name <c>"export"</c>).
    /// </summary>
    Export = 4,

    /// <summary>
    /// Read service/layer metadata and capabilities (wire name <c>"metadata"</c>).
    /// </summary>
    Metadata = 5,

    /// <summary>
    /// Administrative operations on the service/layer (wire name <c>"admin"</c>).
    /// </summary>
    Admin = 6,

    /// <summary>
    /// Synonym for <see cref="Query"/> retained for backward compatibility with
    /// existing <c>"read"</c> grants (wire name <c>"read"</c>).
    /// </summary>
    Read = 7,
}

/// <summary>
/// Wire-name helpers for <see cref="AuthorizationOperation"/>.
/// </summary>
public static class AuthorizationOperationExtensions
{
    /// <summary>
    /// The wildcard operation string that matches every operation in a grant.
    /// </summary>
    public const string Wildcard = "*";

    /// <summary>
    /// Returns the canonical lowercase wire-name for an operation.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The lowercase wire name (e.g. <c>"query"</c>).</returns>
    public static string ToWireName(this AuthorizationOperation operation) => operation switch
    {
        AuthorizationOperation.Query => "query",
        AuthorizationOperation.Insert => "insert",
        AuthorizationOperation.Update => "update",
        AuthorizationOperation.Delete => "delete",
        AuthorizationOperation.Export => "export",
        AuthorizationOperation.Metadata => "metadata",
        AuthorizationOperation.Admin => "admin",
        AuthorizationOperation.Read => "read",
        _ => operation.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Determines whether a grant operation string satisfies the requested
    /// operation. A grant of <see cref="Wildcard"/> satisfies anything; a
    /// <c>"read"</c> grant satisfies <see cref="AuthorizationOperation.Query"/>
    /// (and vice-versa) so legacy read grants cover the canonical read op.
    /// </summary>
    /// <param name="grantOperation">The operation string stored on the grant.</param>
    /// <param name="requested">The operation being authorized.</param>
    /// <returns><see langword="true"/> when the grant covers the request.</returns>
    public static bool Satisfies(string? grantOperation, AuthorizationOperation requested)
    {
        if (string.IsNullOrWhiteSpace(grantOperation))
        {
            return false;
        }

        var grant = grantOperation.Trim();
        if (grant == Wildcard)
        {
            return true;
        }

        // Read/Query are synonyms: a "read" grant covers a Query request and a
        // "query" grant covers a Read request.
        if (IsReadSynonym(requested) && IsReadSynonymString(grant))
        {
            return true;
        }

        return string.Equals(grant, requested.ToWireName(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadSynonym(AuthorizationOperation operation)
        => operation is AuthorizationOperation.Query or AuthorizationOperation.Read;

    private static bool IsReadSynonymString(string operation)
        => string.Equals(operation, "read", StringComparison.OrdinalIgnoreCase)
           || string.Equals(operation, "query", StringComparison.OrdinalIgnoreCase);
}
