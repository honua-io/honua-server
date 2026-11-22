// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.Exports;

// Licence and providers for the export's STAC collection, from metadata Honua holds.
internal static class ExportStacFacts
{
    private const string Proprietary = "proprietary";

    // Metadata licences are canonical SPDX or the literal 'proprietary' (MetadataV2GraphValidation);
    // STAC 1.1 spells anything that is not an SPDX expression as "other".
    public static string? License(MetadataV2ObjectMetadata metadata)
        => string.IsNullOrWhiteSpace(metadata.License) ? null
            : string.Equals(metadata.License, Proprietary, StringComparison.OrdinalIgnoreCase) ? "other"
            : metadata.License.Trim();

    public static string LicenseLabel(MetadataV2ObjectMetadata metadata)
        => License(metadata) switch
        {
            null => "Not stated",
            "other" => "Proprietary",
            var spdx => spdx
        };

    public static JsonArray Providers(ExportCompanionInput input)
    {
        var providers = new JsonArray();
        if (!string.IsNullOrWhiteSpace(input.Metadata.Publisher))
        {
            var producer = new JsonObject { ["name"] = input.Metadata.Publisher.Trim(), ["roles"] = new JsonArray("producer") };
            if (WebUrl(input.Metadata.ContactPoint?.Url) is { } url)
            {
                producer["url"] = url;
            }

            providers.Add(producer);
        }

        var host = new JsonObject
        {
            ["name"] = input.HostName,
            ["roles"] = new JsonArray("host"),
            ["url"] = WebUrl(input.Metadata.ContactPoint?.Url) ?? "https://honua.io"
        };
        if (!string.IsNullOrWhiteSpace(input.Metadata.ContactPoint?.Email))
        {
            host["email"] = input.Metadata.ContactPoint.Email.Trim();
        }

        providers.Add(host);
        return providers;
    }

    private static string? WebUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
}
