// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesSourceService(string PublicName, string? FolderPath)
{
    public static GeoservicesSourceService? FromUrl(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        // Preserve raw segments so URI normalization cannot turn an invalid source folder into root.
        var authorityStart = serviceUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityLength = serviceUrl.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        if (authorityLength < 0) return null;
        var pathStart = authorityStart + authorityLength;
        if (serviceUrl[pathStart] != '/') return null;
        var path = serviceUrl[pathStart..].Split('?', '#')[0].TrimEnd('/');
        var segments = path.Split('/');
        var servicesIndex = Enumerable.Range(1, segments.Length - 1).FirstOrDefault(index =>
            segments[index - 1].Equals("rest", StringComparison.OrdinalIgnoreCase) &&
            segments[index].Equals("services", StringComparison.OrdinalIgnoreCase), -1);
        if (servicesIndex < 0)
            servicesIndex = Array.FindIndex(segments, segment => segment.Equals("services", StringComparison.OrdinalIgnoreCase));
        if (servicesIndex < 0 || segments.Length - servicesIndex < 3 ||
            !MetadataV2ServicePaths.ServiceTypes.Contains(segments[^1])) return null;

        var folders = segments[(servicesIndex + 1)..^2];
        return new(segments[^2], folders.Length == 0 ? null : string.Join('/', folders));
    }
}
