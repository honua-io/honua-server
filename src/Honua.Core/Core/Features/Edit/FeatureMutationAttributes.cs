// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Edit;

public static class FeatureMutationAttributes
{
    public const string WfsGmlIdentifierAttributeName = "__honua_wfs_gml_identifier";
    public const string WfsGmlIdentifierCodeSpaceAttributeName = "__honua_wfs_gml_identifier_codespace";
    public const string WfsGmlNameAttributeName = "__honua_wfs_gml_name";
    public const string WfsGmlDescriptionAttributeName = "__honua_wfs_gml_description";

    public static bool IsReserved(string attributeName)
    {
        return attributeName.Equals(WfsGmlIdentifierAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlIdentifierCodeSpaceAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlNameAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlDescriptionAttributeName, StringComparison.Ordinal);
    }
}
