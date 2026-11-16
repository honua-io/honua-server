// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public static class SpecGrammarVersion
{
    public const string Current = "v1.0";

    public const int CurrentMajor = 1;

    public const int CurrentMinor = 0;

    public const string CurrentOperatorCapability = "v1.0";

    public const string SchemaUrl = "https://honua.io/spec/grammar/v1.0/spec.json";

    public static bool TryParse(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 4 || (trimmed[0] != 'v' && trimmed[0] != 'V'))
        {
            return false;
        }

        var body = trimmed[1..];
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1)
        {
            return false;
        }

        return int.TryParse(body[..dot], out major) && int.TryParse(body[(dot + 1)..], out minor);
    }
}
