// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Geoprocessing.Testing;

public sealed record ArtifactPayload(string MediaType, byte[] Bytes)
{
    public string AsText() => Encoding.UTF8.GetString(Bytes);

    public bool IsGeoJson => MediaType.Contains("geo+json", StringComparison.OrdinalIgnoreCase);

    public static ArtifactPayload Decode(string artifactReference)
    {
        ArgumentNullException.ThrowIfNull(artifactReference);

        if (artifactReference.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = artifactReference.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                var header = artifactReference[5..comma]; // "<media>;base64"
                var payload = artifactReference[(comma + 1)..];
                var mediaType = header;
                var semicolon = header.IndexOf(';', StringComparison.Ordinal);
                if (semicolon >= 0)
                {
                    mediaType = header[..semicolon];
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(payload);
                }
                catch (FormatException)
                {
                    bytes = Encoding.UTF8.GetBytes(payload);
                }

                return new ArtifactPayload(mediaType, bytes);
            }
        }

        return new ArtifactPayload(string.Empty, Encoding.UTF8.GetBytes(artifactReference));
    }
}
