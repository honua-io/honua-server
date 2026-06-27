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

    /// <summary>ServiceProviderConfig resource schema URI.</summary>
    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";

    /// <summary>ResourceType resource schema URI.</summary>
    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";

    /// <summary>Schema-definition resource schema URI.</summary>
    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    /// <summary>SCIM JSON content type.</summary>
    public const string ContentType = "application/scim+json";
}

/// <summary>
/// SCIM 2.0 ServiceProviderConfig resource (RFC 7643 §5). Advertises the optional features the
/// service provider supports so an IdP can adapt its provisioning behavior.
/// </summary>
public sealed class ScimServiceProviderConfig
{
    /// <summary>Schema URIs for this resource.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ServiceProviderConfig];

    /// <summary>HTTP-addressable documentation describing the service provider.</summary>
    public string? DocumentationUri { get; init; }

    /// <summary>PATCH support (RFC 7644 §3.5.2).</summary>
    public required ScimSupported Patch { get; init; }

    /// <summary>Bulk-operation support (RFC 7644 §3.7).</summary>
    public required ScimBulkConfig Bulk { get; init; }

    /// <summary>Filtering support (RFC 7644 §3.4.2.2).</summary>
    public required ScimFilterConfig Filter { get; init; }

    /// <summary>Change-password support.</summary>
    public required ScimSupported ChangePassword { get; init; }

    /// <summary>Sorting support (RFC 7644 §3.4.2.3).</summary>
    public required ScimSupported Sort { get; init; }

    /// <summary>ETag support.</summary>
    public required ScimSupported Etag { get; init; }

    /// <summary>Supported authentication schemes.</summary>
    public IReadOnlyList<ScimAuthenticationScheme> AuthenticationSchemes { get; init; } = [];

    /// <summary>Resource metadata (read-only).</summary>
    public ScimMeta? Meta { get; init; }
}

/// <summary>A simple supported/unsupported feature flag (RFC 7643 §5).</summary>
public sealed class ScimSupported
{
    /// <summary>Whether the feature is supported.</summary>
    public bool Supported { get; init; }
}

/// <summary>Bulk-operation capability descriptor (RFC 7643 §5).</summary>
public sealed class ScimBulkConfig
{
    /// <summary>Whether bulk operations are supported.</summary>
    public bool Supported { get; init; }

    /// <summary>Maximum number of operations per bulk request.</summary>
    public int MaxOperations { get; init; }

    /// <summary>Maximum bulk-request payload size in bytes.</summary>
    public int MaxPayloadSize { get; init; }
}

/// <summary>Filtering capability descriptor (RFC 7643 §5).</summary>
public sealed class ScimFilterConfig
{
    /// <summary>Whether filtering is supported.</summary>
    public bool Supported { get; init; }

    /// <summary>Maximum number of resources returned by a filtered query.</summary>
    public int MaxResults { get; init; }
}

/// <summary>An authentication scheme advertised by the service provider (RFC 7643 §5).</summary>
public sealed class ScimAuthenticationScheme
{
    /// <summary>Scheme type (e.g. "oauthbearertoken", "httpbasic").</summary>
    public string? Type { get; init; }

    /// <summary>Human-readable name.</summary>
    public string? Name { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>HTTP-addressable specification document for the scheme.</summary>
    public string? SpecUri { get; init; }

    /// <summary>Whether this is the primary/preferred scheme.</summary>
    public bool? Primary { get; init; }
}

/// <summary>
/// SCIM 2.0 ResourceType resource (RFC 7643 §6). Describes an endpoint resource (User/Group),
/// the schema that backs it, and where it is hosted.
/// </summary>
public sealed class ScimResourceType
{
    /// <summary>Schema URIs for this resource.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ResourceType];

    /// <summary>Resource type identifier (e.g. "User").</summary>
    public string? Id { get; init; }

    /// <summary>Resource type name.</summary>
    public string? Name { get; init; }

    /// <summary>Relative endpoint the resource is hosted at (e.g. "/Users").</summary>
    public string? Endpoint { get; init; }

    /// <summary>The primary schema URI for this resource type.</summary>
    public string? Schema { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Resource metadata (read-only).</summary>
    public ScimMeta? Meta { get; init; }
}

/// <summary>
/// SCIM 2.0 Schema resource (RFC 7643 §7) describing the attributes of a resource type.
/// </summary>
public sealed class ScimSchemaResource
{
    /// <summary>The schema URI this definition describes.</summary>
    public string? Id { get; init; }

    /// <summary>Schema name (e.g. "User").</summary>
    public string? Name { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Attribute definitions for the schema.</summary>
    public IReadOnlyList<ScimAttributeDefinition> Attributes { get; init; } = [];

    /// <summary>Resource metadata (read-only).</summary>
    public ScimMeta? Meta { get; init; }
}

/// <summary>A SCIM attribute definition (RFC 7643 §7).</summary>
public sealed class ScimAttributeDefinition
{
    /// <summary>Attribute name.</summary>
    public string? Name { get; init; }

    /// <summary>Data type (e.g. "string", "boolean", "complex", "reference").</summary>
    public string? Type { get; init; }

    /// <summary>Whether the attribute is multi-valued.</summary>
    public bool MultiValued { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the attribute is required.</summary>
    public bool Required { get; init; }

    /// <summary>Whether string comparison is case-sensitive.</summary>
    public bool CaseExact { get; init; }

    /// <summary>Mutability ("readOnly", "readWrite", "immutable", "writeOnly").</summary>
    public string? Mutability { get; init; }

    /// <summary>Return characteristics ("always", "never", "default", "request").</summary>
    public string? Returned { get; init; }

    /// <summary>Uniqueness constraint ("none", "server", "global").</summary>
    public string? Uniqueness { get; init; }

    /// <summary>Sub-attribute definitions for a complex attribute.</summary>
    public IReadOnlyList<ScimAttributeDefinition>? SubAttributes { get; init; }
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
[JsonSerializable(typeof(ScimServiceProviderConfig))]
[JsonSerializable(typeof(ScimResourceType))]
[JsonSerializable(typeof(ScimSchemaResource))]
[JsonSerializable(typeof(ScimListResponse<ScimResourceType>))]
[JsonSerializable(typeof(ScimListResponse<ScimSchemaResource>))]
internal sealed partial class ScimJsonContext : JsonSerializerContext
{
}
