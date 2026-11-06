// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.Catalog;

internal static class GeoServicesCatalogDirectory
{
    public static string[] FolderNames(IEnumerable<ServiceDirectoryEntry> entries)
        => entries.Select(entry => entry.FolderName).OfType<string>()
            .Append(PrintingToolsServiceCatalog.FolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static ServiceDirectoryEntry[] InFolder(IEnumerable<ServiceDirectoryEntry> entries, string? folderName)
        => entries.Where(entry => string.Equals(entry.FolderName, folderName, StringComparison.OrdinalIgnoreCase)).ToArray();
}
