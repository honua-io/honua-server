// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2FolderPreflight
{
    public static IReadOnlyList<string> Inspect(MetadataV2Graph graph, MetadataV2Service service)
    {
        var issues = new List<string>();
        if (!MetadataV2ServicePaths.IsPublicName(service.EffectivePublicName, service.FolderId))
            issues.Add("public-name-requires-mapping");
        var siblings = graph.Services.Where(s => string.Equals(s.Metadata.Name, service.Metadata.Name, StringComparison.OrdinalIgnoreCase));
        if (siblings.Select(s => s.Metadata.Name).Distinct(StringComparer.Ordinal).Skip(1).Any())
            issues.Add("ambiguous-logical-name");
        return issues;
    }
}
