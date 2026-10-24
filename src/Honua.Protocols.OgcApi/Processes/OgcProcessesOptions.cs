// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Configuration options for the OGC API Processes adapter.
/// </summary>
internal sealed class OgcProcessesOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "OgcProcesses";

    /// <summary>
    /// Maximum number of active jobs returned per list request.
    /// </summary>
    public int DefaultJobLimit { get; set; } = 100;
}
