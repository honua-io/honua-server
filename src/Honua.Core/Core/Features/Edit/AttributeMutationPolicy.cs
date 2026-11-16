// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

public static class AttributeMutationPolicy
{
    public static bool IsReadOnlyOnUpdate(MetadataV2Object resource, MetadataV2Field field)
        => !field.Editable || field.Name.Equals(resource.Editing?.GlobalIdField, StringComparison.OrdinalIgnoreCase);

    internal static ImmutableArray<string> ProtectedFieldNames(MetadataV2Object resource)
    {
        var names = resource.SchemaFields
            .Where(field => IsReadOnlyOnUpdate(resource, field))
            .Select(field => field.Name);
        var globalIdField = resource.Editing?.GlobalIdField;
        if (!string.IsNullOrWhiteSpace(globalIdField))
        {
            names = names.Append(globalIdField);
        }

        var protectedNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        return protectedNames.IsEmpty ? default : protectedNames;
    }
}
