// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public static class LayerPublicationNamespace
{
    public static string? ResolveTenant(string? publicationNamespace, string? trustedTenant)
    {
        if (!IsValid(publicationNamespace))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Namespace must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
        if (publicationNamespace is not null && string.IsNullOrWhiteSpace(trustedTenant))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Namespaced publication requires a resolved tenant.");
        return publicationNamespace is null ? null : trustedTenant;
    }

    public static bool IsValid(string? value) => value is null ||
        (value.Length is >= 1 and <= 128 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
}
