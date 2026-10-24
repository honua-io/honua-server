// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.Catalog;

/// <summary>
/// Built-in Utilities/PrintingTools GPServer catalog identity.
/// The print handlers own execution; this type only publishes the service.
/// </summary>
internal static class PrintingToolsServiceCatalog
{
    internal const string FolderName = "Utilities";
    internal const string ServiceName = "PrintingTools";
    internal const string QualifiedName = "Utilities/PrintingTools";
    internal const string ProtocolName = "GPServer";
    internal const string ExportTaskName = "Export Web Map Task";
    internal const string LayoutTaskName = "Get Layout Templates Info Task";
    internal const string ExportSoapTaskName = "ExportWebMapTask";
    internal const string LayoutSoapTaskName = "GetLayoutTemplatesInfoTask";
    internal const string ExecutionType = "esriExecutionTypeSynchronous";

    internal static bool IsFolder(string? name)
        => string.Equals(name?.Trim(), FolderName, StringComparison.OrdinalIgnoreCase);

    internal static ServiceDirectoryEntry CreateEntry(string baseUrl)
        => new()
        {
            Name = QualifiedName,
            Type = ProtocolName,
            Url = $"{baseUrl}/rest/services/{EscapeCatalogName(QualifiedName)}/{ProtocolName}"
        };

    /// <summary>
    /// Escapes each path segment and keeps slashes. Names without a slash stay
    /// byte-identical to <see cref="Uri.EscapeDataString(string)"/>.
    /// </summary>
    internal static string EscapeCatalogName(string name)
        => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
}
