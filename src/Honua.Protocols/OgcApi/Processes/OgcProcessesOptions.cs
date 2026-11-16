// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

internal sealed class OgcProcessesOptions
{
    public const string SectionName = "OgcProcesses";

    public int DefaultJobLimit { get; set; } = 100;
}
