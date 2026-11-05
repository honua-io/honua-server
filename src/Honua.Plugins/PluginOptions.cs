// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins;

public sealed class PluginOptions
{
    public const string SectionName = "Plugins";

    public bool Enabled { get; set; } = true;
}
