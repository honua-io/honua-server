// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Services;

public static class CsvFieldFormatter
{
    public static string Escape(string? value)
    {
        if (value is null)
            return string.Empty;
        if (value.Length == 0)
            return "\"\"";
        if (value.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || value.Contains('\r'))
            return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
        return value;
    }
}
