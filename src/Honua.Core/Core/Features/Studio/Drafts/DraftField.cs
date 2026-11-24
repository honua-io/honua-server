// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Drafts;

internal static class DraftField
{
    internal static string? NormalizeOptional(string? value, string name, List<PackageDraftFinding> errors)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 0)
        {
            return trimmed;
        }

        errors.Add(new PackageDraftFinding
        {
            Code = name + "Invalid",
            Path = "/" + name,
            Message = $"{name} was supplied but is blank; omit it instead.",
        });
        return null;
    }
}
