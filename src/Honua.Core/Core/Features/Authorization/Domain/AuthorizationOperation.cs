// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public enum AuthorizationOperation
{
    Query = 0,

    Insert = 1,

    Update = 2,

    Delete = 3,

    Export = 4,

    Metadata = 5,

    Admin = 6,

    Read = 7,

    Approve = 8,
}

public static class AuthorizationOperationExtensions
{
    public const string Wildcard = "*";

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
        AuthorizationOperation.Approve => "approve",
        _ => operation.ToString().ToLowerInvariant(),
    };

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
