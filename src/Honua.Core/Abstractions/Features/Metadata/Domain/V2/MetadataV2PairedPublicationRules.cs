// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static partial class MetadataV2PairedPublicationRules
{
    public static string? Problem(MetadataV2PairedPublication recipe, MetadataV2Object resource)
    {
        if (recipe.PublicationId is null || !PublicationIdPattern().IsMatch(recipe.PublicationId)
            || recipe.IdentityField is null || !FieldNamePattern().IsMatch(recipe.IdentityField)
            || recipe.MinZoom < 0 || recipe.MaxZoom < recipe.MinZoom || recipe.MaxZoom > 22
            || recipe.MaxTiles is < 1 or > 1000000)
        {
            return "Paired publication requires a portable publication id and identity field, zooms 0–22 and a bounded tile count.";
        }
        return resource.SchemaFields.Any(field => field.Name == recipe.IdentityField
            && field.Type == MetadataV2FieldType.String && !field.Hidden)
            ? null : "Paired publication identity must be a visible text field of the layer.";
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicationIdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNamePattern();
}
