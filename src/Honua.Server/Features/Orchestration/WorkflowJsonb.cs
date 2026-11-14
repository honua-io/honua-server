// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Honua.Server.Features.Orchestration;

// PostgreSQL jsonb refuses the NUL character (SQLSTATE 22P05). Workflow records carry tool and job
// output such as step error messages, so a NUL there would fail every write of the run and leave
// it active forever. Each NUL is stored as U+FFFD instead.
internal static partial class WorkflowJsonb
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => EscapedNul().Replace(JsonSerializer.Serialize(value, typeInfo), "$1\\uFFFD");

    // An escaped NUL is "\u0000" preceded by an even number of backslashes; an odd count means the
    // text is a literal backslash followed by "u0000" and stays as it is.
    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedNul();
}
