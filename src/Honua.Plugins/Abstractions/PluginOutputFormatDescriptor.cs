// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public sealed record PluginOutputFormatDescriptor(
    string FormatId,
    string MediaType,
    string FileExtension,
    string PluginId);
