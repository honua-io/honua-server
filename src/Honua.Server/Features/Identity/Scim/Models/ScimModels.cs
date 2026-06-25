// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Identity.Scim.Models;

/// <summary>
/// SCIM 2.0 well-known schema URIs and constants (RFC 7643 / RFC 7644).
/// </summary>
internal static class ScimSchemas
{
    /// <summary>Core User resource schema URI.</summary>
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";

    /// <summary>Core Group resource schema URI.</summary>
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";

    /// <summary>ListResponse message schema URI.</summary>
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";

    /// <summary>PatchOp message schema URI.</summary>
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    /// <summary>Error message schema URI.</summary>
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";

    /// <summary>SCIM JSON content type.</summary>
    public const string ContentType = "application/scim+json";
}

/// <summary>
/// SCIM 2.0 User resource (RFC 7643 §4.1). Only the attributes Honua maps onto its identity
/// model are surfaced; unknown attributes from the IdP are ignored on input.
/// </summary>
public sealed class ScimUser
{
    /// <summary>Schema URIs for this resource.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.User];

    /// <summary>Server-assigned unique identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Unique login identifier owned by the identity provider.</summary>
    public string? UserName { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether the account is active.</summary>
    public bool Active { get; init; } = true;

    /// <summary>Email addresses.</summary>
    public IReadOnlyList<ScimEmail>? Emails { get; init; }

    /// <summary>Resource metadata (read-only).</summary>
    public ScimMeta? Meta { get; init; }
}

/// <summary>SCIM multi-valued email attribute.</summary>
public sealed class ScimEmail
{
    /// <summary>The email address.</summary>
    public string? Value { get; init; }

    /// <summary>Whether this is the primary email.</summary>
    public bool? Primary { get; init; }

    /// <summary>Type label (e.g. "work").</summary>
    public string? Type { get; init; }
}

/// <summary>
/// SCIM 2.0 Group resource (RFC 7643 §4.2).
/// </summary>
public sealed class ScimGroupResource
{
    /// <summary>Schema URIs for this resource.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.Group];

    /// <summary>Server-assigned unique identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Group display name; maps to the Honua role name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Group members.</summary>
    public IReadOnlyList<ScimMember>? Members { get; init; }

    /// <summary>Resource metadata (read-only).</summary>
    public ScimMeta? Meta { get; init; }
}

/// <summary>SCIM group member reference.</summary>
public sealed class ScimMember
{
    /// <summary>The member user's identifier.</summary>
    public string? Value { get; init; }

    /// <summary>Optional display name of the member.</summary>
    public string? Display { get; init; }
}

/// <summary>SCIM resource metadata (RFC 7643 §3.1).</summary>
public sealed class ScimMeta
{
    /// <summary>Resource type ("User" or "Group").</summary>
    public string? ResourceType { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>Last-modified timestamp.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Canonical resource location.</summary>
    public string? Location { get; init; }
}

/// <summary>
/// SCIM ListResponse envelope (RFC 7644 §3.4.2).
/// </summary>
/// <typeparam name="T">The contained resource type.</typeparam>
public sealed class ScimListResponse<T>
{
    /// <summary>Schema URIs for this message.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ListResponse];

    /// <summary>Total number of resources matching the query.</summary>
    public int TotalResults { get; init; }

    /// <summary>1-based index of the first returned resource.</summary>
    public int StartIndex { get; init; }

    /// <summary>Number of resources in this page.</summary>
    public int ItemsPerPage { get; init; }

    /// <summary>The returned resources.</summary>
    public IReadOnlyList<T> Resources { get; init; } = [];
}

/// <summary>
/// SCIM PatchOp request (RFC 7644 §3.5.2).
/// </summary>
public sealed class ScimPatchRequest
{
    /// <summary>Schema URIs for this message.</summary>
    public IReadOnlyList<string>? Schemas { get; init; }

    /// <summary>The patch operations to apply.</summary>
    public IReadOnlyList<ScimPatchOperation>? Operations { get; init; }
}

/// <summary>A single SCIM patch operation.</summary>
public sealed class ScimPatchOperation
{
    /// <summary>Operation: "add", "remove", or "replace" (case-insensitive).</summary>
    public string? Op { get; init; }

    /// <summary>Target attribute path (e.g. "active", "members").</summary>
    public string? Path { get; init; }

    /// <summary>Operation value (shape depends on <see cref="Path"/>).</summary>
    public System.Text.Json.JsonElement? Value { get; init; }
}

/// <summary>
/// SCIM error response (RFC 7644 §3.12).
/// </summary>
public sealed class ScimError
{
    /// <summary>Schema URIs for this message.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.Error];

    /// <summary>HTTP status code as a string.</summary>
    public required string Status { get; init; }

    /// <summary>Human-readable detail.</summary>
    public string? Detail { get; init; }

    /// <summary>SCIM-specific error type (e.g. "uniqueness", "invalidValue").</summary>
    public string? ScimType { get; init; }
}

/// <summary>
/// JSON source-generation context for SCIM wire models (AOT-safe). SCIM emits its own RFC
/// 7643/7644 envelope shapes rather than the generic Honua <c>ApiResponse</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ScimUser))]
[JsonSerializable(typeof(ScimGroupResource))]
[JsonSerializable(typeof(ScimListResponse<ScimUser>))]
[JsonSerializable(typeof(ScimListResponse<ScimGroupResource>))]
[JsonSerializable(typeof(ScimPatchRequest))]
[JsonSerializable(typeof(ScimError))]
internal sealed partial class ScimJsonContext : JsonSerializerContext
{
}
