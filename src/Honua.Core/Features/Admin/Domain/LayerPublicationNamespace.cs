// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public static class LayerPublicationNamespace
{
    public static bool IsValid(string? value) => value is null ||
        (value.Length is >= 1 and <= 128 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
}
