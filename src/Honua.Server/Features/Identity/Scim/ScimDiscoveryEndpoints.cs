// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Identity.Scim.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Scim;

/// <summary>
/// SCIM 2.0 service-provider discovery documents (#2154, RFC 7643 §5-7): the
/// <c>/ServiceProviderConfig</c>, <c>/ResourceTypes</c>, and <c>/Schemas</c> endpoints an IdP
/// queries to learn which optional features, resource types, and attribute schemas Honua
/// supports. These are static, provider-agnostic descriptions of the implemented surface and
/// are gated by the same bearer token as the rest of the SCIM API.
/// </summary>
internal static partial class ScimEndpoints
{
    // ---- ServiceProviderConfig --------------------------------------------------------

    private static IResult HandleServiceProviderConfig(
        HttpContext context,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        return ScimJson(ServiceProviderConfig, ScimJsonContext.Default.ScimServiceProviderConfig, StatusCodes.Status200OK);
    }

    // ---- ResourceTypes ----------------------------------------------------------------

    private static IResult HandleResourceTypes(
        HttpContext context,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var response = new ScimListResponse<ScimResourceType>
        {
            TotalResults = ResourceTypes.Count,
            StartIndex = 1,
            ItemsPerPage = ResourceTypes.Count,
            Resources = ResourceTypes,
        };

        return ScimJson(response, ScimJsonContext.Default.ScimListResponseScimResourceType, StatusCodes.Status200OK);
    }

    private static IResult HandleResourceType(
        string id,
        HttpContext context,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var match = ResourceTypes.FirstOrDefault(rt => string.Equals(rt.Id, id, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? ScimErrorResult(StatusCodes.Status404NotFound, "ResourceType not found.")
            : ScimJson(match, ScimJsonContext.Default.ScimResourceType, StatusCodes.Status200OK);
    }

    // ---- Schemas ----------------------------------------------------------------------

    private static IResult HandleSchemas(
        HttpContext context,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var response = new ScimListResponse<ScimSchemaResource>
        {
            TotalResults = SchemaDefinitions.Count,
            StartIndex = 1,
            ItemsPerPage = SchemaDefinitions.Count,
            Resources = SchemaDefinitions,
        };

        return ScimJson(response, ScimJsonContext.Default.ScimListResponseScimSchemaResource, StatusCodes.Status200OK);
    }

    private static IResult HandleSchema(
        string id,
        HttpContext context,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var match = SchemaDefinitions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? ScimErrorResult(StatusCodes.Status404NotFound, "Schema not found.")
            : ScimJson(match, ScimJsonContext.Default.ScimSchemaResource, StatusCodes.Status200OK);
    }

    // ---- Static discovery documents ---------------------------------------------------

    private static readonly ScimServiceProviderConfig ServiceProviderConfig = new()
    {
        DocumentationUri = "https://docs.honua.io/reference/compatibility/idp-conformance-matrix",
        Patch = new ScimSupported { Supported = true },
        Bulk = new ScimBulkConfig { Supported = false, MaxOperations = 0, MaxPayloadSize = 0 },
        Filter = new ScimFilterConfig { Supported = true, MaxResults = MaxPageSize },
        ChangePassword = new ScimSupported { Supported = false },
        Sort = new ScimSupported { Supported = false },
        Etag = new ScimSupported { Supported = false },
        AuthenticationSchemes =
        [
            new ScimAuthenticationScheme
            {
                Type = "oauthbearertoken",
                Name = "OAuth Bearer Token",
                Description = "Authentication via the OAuth 2.0 Bearer Token standard.",
                SpecUri = "https://www.rfc-editor.org/info/rfc6750",
                Primary = true,
            },
        ],
        Meta = new ScimMeta
        {
            ResourceType = "ServiceProviderConfig",
            Location = "/scim/v2/ServiceProviderConfig",
        },
    };

    private static readonly IReadOnlyList<ScimResourceType> ResourceTypes =
    [
        new ScimResourceType
        {
            Id = "User",
            Name = "User",
            Endpoint = "/Users",
            Schema = ScimSchemas.User,
            Description = "User Account",
            Meta = new ScimMeta { ResourceType = "ResourceType", Location = "/scim/v2/ResourceTypes/User" },
        },
        new ScimResourceType
        {
            Id = "Group",
            Name = "Group",
            Endpoint = "/Groups",
            Schema = ScimSchemas.Group,
            Description = "Group",
            Meta = new ScimMeta { ResourceType = "ResourceType", Location = "/scim/v2/ResourceTypes/Group" },
        },
    ];

    private static readonly IReadOnlyList<ScimSchemaResource> SchemaDefinitions =
    [
        new ScimSchemaResource
        {
            Id = ScimSchemas.User,
            Name = "User",
            Description = "User Account",
            Meta = new ScimMeta { ResourceType = "Schema", Location = "/scim/v2/Schemas/" + ScimSchemas.User },
            Attributes =
            [
                StringAttribute("userName", required: true, uniqueness: "server"),
                StringAttribute("displayName"),
                new ScimAttributeDefinition
                {
                    Name = "active",
                    Type = "boolean",
                    MultiValued = false,
                    Description = "A Boolean value indicating the user's administrative status.",
                    Required = false,
                    CaseExact = false,
                    Mutability = "readWrite",
                    Returned = "default",
                    Uniqueness = "none",
                },
                new ScimAttributeDefinition
                {
                    Name = "emails",
                    Type = "complex",
                    MultiValued = true,
                    Description = "Email addresses for the user.",
                    Required = false,
                    CaseExact = false,
                    Mutability = "readWrite",
                    Returned = "default",
                    Uniqueness = "none",
                    SubAttributes =
                    [
                        StringAttribute("value"),
                        StringAttribute("type"),
                        new ScimAttributeDefinition
                        {
                            Name = "primary",
                            Type = "boolean",
                            MultiValued = false,
                            Required = false,
                            CaseExact = false,
                            Mutability = "readWrite",
                            Returned = "default",
                            Uniqueness = "none",
                        },
                    ],
                },
            ],
        },
        new ScimSchemaResource
        {
            Id = ScimSchemas.Group,
            Name = "Group",
            Description = "Group",
            Meta = new ScimMeta { ResourceType = "Schema", Location = "/scim/v2/Schemas/" + ScimSchemas.Group },
            Attributes =
            [
                StringAttribute("displayName", required: true),
                new ScimAttributeDefinition
                {
                    Name = "members",
                    Type = "complex",
                    MultiValued = true,
                    Description = "A list of members of the Group.",
                    Required = false,
                    CaseExact = false,
                    Mutability = "readWrite",
                    Returned = "default",
                    Uniqueness = "none",
                    SubAttributes =
                    [
                        StringAttribute("value"),
                        StringAttribute("display"),
                    ],
                },
            ],
        },
    ];

    private static ScimAttributeDefinition StringAttribute(string name, bool required = false, string uniqueness = "none")
        => new()
        {
            Name = name,
            Type = "string",
            MultiValued = false,
            Required = required,
            CaseExact = false,
            Mutability = "readWrite",
            Returned = "default",
            Uniqueness = uniqueness,
        };
}
