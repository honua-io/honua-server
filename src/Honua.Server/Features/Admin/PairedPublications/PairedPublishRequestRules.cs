// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Server.Features.Admin.TileOperations;

namespace Honua.Server.Features.Admin.PairedPublications;

internal static partial class PairedPublishRequestRules
{
    public static string? Problem(TileOperationStartRequest request)
    {
        if (!request.LayerId.HasValue || string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return "Paired publish operations require 'serviceId' and 'layerId'.";
        }

        if (request.PublicationId is null || !IsPublicationId(request.PublicationId))
        {
            return "Property 'publicationId' must be 3-64 lowercase letters, digits or hyphens, starting with a letter or digit.";
        }

        if (request.IdentityField is null || !IsPortableFieldName(request.IdentityField))
        {
            return "Property 'identityField' must name the layer's public text identity field.";
        }

        // Both artifacts must hold the same features; a tile window would clip only one of them.
        return request.Bbox is null
            ? null
            : "Property 'bbox' does not apply to paired publish; tile coverage follows the captured features.";
    }

    public static bool IsPublicationId(string? name) => name is not null && PublicationIdPattern().IsMatch(name);

    public static bool IsPortableFieldName(string name) => FieldNamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicationIdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNamePattern();
}
