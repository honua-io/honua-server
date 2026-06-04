// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text.Json;

namespace Honua.Core.Features.Scene.Conversion;

/// <summary>
/// Parses Esri I3S scene-layer descriptors. The descriptor is the
/// <c>3dSceneLayer.json</c> document either served by a SceneServer
/// (<c>/SceneServer/layers/0</c>) or stored inside a <c>.slpk</c> package,
/// where it is conventionally gzip-compressed as
/// <c>3dSceneLayer.json.gz</c>.
/// </summary>
/// <remarks>
/// I3S is an OGC Community Standard (open) and <c>.slpk</c> is a standard zip
/// package container, so both are safe to read from the public spec. This
/// reader performs no geometry decode; it only maps the JSON descriptor into
/// the strongly-typed <see cref="I3sSceneLayerDocument"/> model.
/// </remarks>
public static class I3sSceneLayerReader
{
    /// <summary>Conventional descriptor name inside an SLPK / on a SceneServer.</summary>
    public const string SceneLayerEntryName = "3dSceneLayer.json";

    /// <summary>Gzip-compressed descriptor name inside an SLPK.</summary>
    public const string SceneLayerGzipEntryName = "3dSceneLayer.json.gz";

    /// <summary>
    /// Parses a raw <c>3dSceneLayer.json</c> byte payload into the descriptor
    /// model. Throws <see cref="I3sConversionException"/> with
    /// <see cref="I3sConversionErrorReason.MalformedSceneLayer"/> when the bytes
    /// are not valid JSON or do not bind to the descriptor shape.
    /// </summary>
    /// <param name="utf8Json">UTF-8 encoded scene-layer JSON.</param>
    public static I3sSceneLayerDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var document = JsonSerializer.Deserialize(
                utf8Json,
                I3sSceneLayerJsonContext.Default.I3sSceneLayerDocument);

            if (document is null)
            {
                throw new I3sConversionException(
                    I3sConversionErrorReason.MalformedSceneLayer,
                    "The I3S scene-layer descriptor was empty or null.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.MalformedSceneLayer,
                "The I3S scene-layer descriptor was not valid JSON.",
                ex);
        }
    }

    /// <summary>
    /// Reads and parses the <c>3dSceneLayer.json</c> descriptor from an open
    /// <c>.slpk</c> archive stream. The stream must be seekable. Looks for the
    /// gzip-compressed entry first (the SLPK convention) and falls back to an
    /// uncompressed descriptor entry. Throws
    /// <see cref="I3sConversionException"/> when the archive is invalid or the
    /// descriptor is missing.
    /// </summary>
    /// <param name="slpkStream">A seekable stream positioned at the start of an SLPK zip archive.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="slpkStream"/> open after reading.</param>
    public static I3sSceneLayerDocument ReadFromSlpk(Stream slpkStream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(slpkStream);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(slpkStream, ZipArchiveMode.Read, leaveOpen);
        }
        catch (InvalidDataException ex)
        {
            throw new I3sConversionException(
                I3sConversionErrorReason.InvalidArchive,
                "The .slpk package is not a valid zip archive.",
                ex);
        }

        using (archive)
        {
            var gzipEntry = FindEntry(archive, SceneLayerGzipEntryName);
            if (gzipEntry is not null)
            {
                using var raw = gzipEntry.Open();
                using var gzip = new GZipStream(raw, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                gzip.CopyTo(buffer);
                return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }

            var plainEntry = FindEntry(archive, SceneLayerEntryName);
            if (plainEntry is not null)
            {
                using var raw = plainEntry.Open();
                using var buffer = new MemoryStream();
                raw.CopyTo(buffer);
                return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }
        }

        throw new I3sConversionException(
            I3sConversionErrorReason.MissingSceneLayer,
            "The .slpk package does not contain a 3dSceneLayer.json descriptor.");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        // SLPK stores the descriptor at the archive root, but match by the
        // trailing filename so a package that nests it under a folder still
        // resolves. Case-insensitive to tolerate producers that vary casing.
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var slash = name.LastIndexOf('/');
            var leaf = slash >= 0 ? name[(slash + 1)..] : name;
            if (string.Equals(leaf, entryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
