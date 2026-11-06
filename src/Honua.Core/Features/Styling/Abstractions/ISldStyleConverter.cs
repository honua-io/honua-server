// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

// Implementations must reject malformed and unsafe XML and must surface unsupported constructs as warnings.
public interface ISldStyleConverter
{
    SldStyleConversionResult Convert(string sldXml);
}

public sealed record SldStyleConversionResult(
    string? MapLibreLayersJson,
    string? DetectedSldVersion,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0 || MapLibreLayersJson == null;
}
