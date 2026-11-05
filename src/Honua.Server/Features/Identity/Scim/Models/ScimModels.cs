// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Identity.Scim.Models;

internal static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";

    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";

    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";

    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";

    public const string ServiceProviderConfig = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";

    public const string ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";

    public const string Schema = "urn:ietf:params:scim:schemas:core:2.0:Schema";

    public const string ContentType = "application/scim+json";
}

public sealed class ScimServiceProviderConfig
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ServiceProviderConfig];

    public string? DocumentationUri { get; init; }

    public required ScimSupported Patch { get; init; }

    public required ScimBulkConfig Bulk { get; init; }

    public required ScimFilterConfig Filter { get; init; }

    public required ScimSupported ChangePassword { get; init; }

    public required ScimSupported Sort { get; init; }

    public required ScimSupported Etag { get; init; }

    public IReadOnlyList<ScimAuthenticationScheme> AuthenticationSchemes { get; init; } = [];

    public ScimMeta? Meta { get; init; }
}

public sealed class ScimSupported
{
    public bool Supported { get; init; }
}

public sealed class ScimBulkConfig
{
    public bool Supported { get; init; }

    public int MaxOperations { get; init; }

    public int MaxPayloadSize { get; init; }
}

public sealed class ScimFilterConfig
{
    public bool Supported { get; init; }

    public int MaxResults { get; init; }
}

public sealed class ScimAuthenticationScheme
{
    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? SpecUri { get; init; }

    public bool? Primary { get; init; }
}

public sealed class ScimResourceType
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ResourceType];

    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Endpoint { get; init; }

    public string? Schema { get; init; }

    public string? Description { get; init; }

    public ScimMeta? Meta { get; init; }
}

public sealed class ScimSchemaResource
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<ScimAttributeDefinition> Attributes { get; init; } = [];

    public ScimMeta? Meta { get; init; }
}

public sealed class ScimAttributeDefinition
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    public bool MultiValued { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }

    public bool CaseExact { get; init; }

    public string? Mutability { get; init; }

    public string? Returned { get; init; }

    public string? Uniqueness { get; init; }

    public IReadOnlyList<ScimAttributeDefinition>? SubAttributes { get; init; }
}

// Unknown attributes from the identity provider are ignored on input.
public sealed class ScimUser
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.User];

    public string? Id { get; init; }

    public string? ExternalId { get; init; }

    public string? UserName { get; init; }

    public string? DisplayName { get; init; }

    public bool Active { get; init; } = true;

    public IReadOnlyList<ScimEmail>? Emails { get; init; }

    public ScimMeta? Meta { get; init; }
}

public sealed class ScimEmail
{
    public string? Value { get; init; }

    public bool? Primary { get; init; }

    public string? Type { get; init; }
}

public sealed class ScimGroupResource
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.Group];

    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public IReadOnlyList<ScimMember>? Members { get; init; }

    public ScimMeta? Meta { get; init; }
}

public sealed class ScimMember
{
    public string? Value { get; init; }

    public string? Display { get; init; }
}

public sealed class ScimMeta
{
    public string? ResourceType { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string? Location { get; init; }
}

public sealed class ScimListResponse<T>
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.ListResponse];

    public int TotalResults { get; init; }

    public int StartIndex { get; init; }

    public int ItemsPerPage { get; init; }

    public IReadOnlyList<T> Resources { get; init; } = [];
}

public sealed class ScimPatchRequest
{
    public IReadOnlyList<string>? Schemas { get; init; }

    public IReadOnlyList<ScimPatchOperation>? Operations { get; init; }
}

public sealed class ScimPatchOperation
{
    public string? Op { get; init; }

    public string? Path { get; init; }

    public System.Text.Json.JsonElement? Value { get; init; }
}

public sealed class ScimError
{
    public IReadOnlyList<string> Schemas { get; init; } = [ScimSchemas.Error];

    public required string Status { get; init; }

    public string? Detail { get; init; }

    public string? ScimType { get; init; }
}

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
