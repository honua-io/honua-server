// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

internal static class ReplacementAttributePreservation
{
    internal static ImmutableArray<string> ProtectedFieldNames(MetadataV2Resource resource)
    {
        var names = resource.SchemaFields
            .Where(field => !field.Editable)
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
