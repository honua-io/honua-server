// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// The decoded payload of a single geoprocessing artifact reference (GP Devkit P6,
/// issue #2127). Executors publish artifacts as base64 data URIs
/// (<c>data:&lt;media&gt;;base64,&lt;payload&gt;</c>); this splits the media type from the
/// raw bytes so the golden comparators can pick a comparator by media type and decode the
/// bytes as text/JSON.
/// </summary>
/// <param name="MediaType">
/// The data-URI media type (e.g. <c>application/geo+json</c>, <c>text/csv</c>), or an empty
/// string for a non-data-URI artifact reference.
/// </param>
/// <param name="Bytes">The decoded artifact bytes.</param>
public sealed record ArtifactPayload(string MediaType, byte[] Bytes)
{
    /// <summary>Decodes the payload bytes as UTF-8 text.</summary>
    public string AsText() => Encoding.UTF8.GetString(Bytes);

    /// <summary>
    /// Whether the artifact's media type denotes vector GeoJSON (<c>application/geo+json</c>).
    /// Plain <c>application/json</c> is deliberately NOT treated as GeoJSON — geometry ops
    /// stamp the <c>geo+json</c> media type, while scalar/metric ops (e.g. area/length) emit
    /// plain <c>application/json</c> and must route to the structural comparator.
    /// </summary>
    public bool IsGeoJson => MediaType.Contains("geo+json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a base64 data URI (or a raw artifact reference) into an
    /// <see cref="ArtifactPayload"/>. A non-data-URI reference is returned with an empty
    /// media type and its UTF-8 bytes.
    /// </summary>
    /// <param name="artifactReference">The artifact reference an executor published.</param>
    /// <returns>The decoded payload.</returns>
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
