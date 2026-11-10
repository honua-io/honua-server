// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

[Flags]
public enum PluginCapability
{
    None = 0,

    BackgroundExecution = 1 << 0,

    CustomEndpoints = 1 << 1,

    OutputFormats = 1 << 2,

    DataStore = 1 << 3,
}
