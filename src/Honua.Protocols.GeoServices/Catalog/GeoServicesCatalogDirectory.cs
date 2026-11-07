// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.Catalog;

internal static class GeoServicesCatalogDirectory
{
    public static ServiceDirectoryEntry CreateEntry(MetadataV2Graph graph, MetadataV2Service service, string serviceType, string baseUrl)
        => new()
        {
            Name = MetadataV2ServicePaths.QualifiedName(graph, service),
            LogicalName = service.Metadata.Name,
            FolderName = MetadataV2ServicePaths.FolderName(graph, service),
            DisplayName = service.Metadata.Title ?? service.EffectivePublicName,
            Type = serviceType,
            Url = $"{baseUrl}{GeoServicesServiceUrls.CanonicalPath(graph, service)}/{serviceType}"
        };

    public static string[] FolderNames(IEnumerable<ServiceDirectoryEntry> entries)
        => entries.Select(entry => entry.FolderName).OfType<string>()
            .Append(PrintingToolsServiceCatalog.FolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static ServiceDirectoryEntry[] InFolder(IEnumerable<ServiceDirectoryEntry> entries, string? folderName)
        => entries.Where(entry => string.Equals(entry.FolderName, folderName, StringComparison.OrdinalIgnoreCase)).ToArray();
}
