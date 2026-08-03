// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared helpers for reading the canonical step inputs the geoprocessing submit
/// path projects onto an <c>ExecutionJobSpec</c> parameter bag.
/// </summary>
internal static class GdalJobInputReader
{
    /// <summary>
    /// Reads either the legacy bounded base64 input or the v2 typed raster descriptor.
    /// Object-store descriptors become GDAL VSI paths; credentials remain execution-owned
    /// worker environment state and never travel in the job payload.
    /// </summary>
    public static bool TryGetRasterInput(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        long maxInlineBytes,
        out GdalRasterInput input,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        input = default;
        error = "";
        var hasTyped = parameters.TryGetValue(
            GdalWorkerParameterKeys.StepRasterSourcePrefix + name,
            out var descriptorJson);
        var hasLegacy = parameters.ContainsKey(GdalWorkerParameterKeys.StepInputPrefix + name);
        if (hasTyped && hasLegacy)
        {
            error = $"input '{name}' has both typed and legacy source representations";
            return false;
        }

        if (!hasTyped)
        {
            if (!TryGetBase64Input(parameters, name, maxInlineBytes, out var bytes, out error))
            {
                return false;
            }

            input = GdalRasterInput.Inline(bytes);
            return true;
        }

        RasterSourceDescriptor descriptor;
        try
        {
            descriptor = RasterSourceJson.Deserialize(descriptorJson!);
        }
        catch (JsonException)
        {
            error = $"typed raster input '{name}' is not a valid descriptor";
            return false;
        }

        var validation = RasterSourceDescriptorValidator.Validate(
            descriptor,
            new RasterSourceValidationOptions
            {
                MaxInlineBytes = (int)Math.Min(maxInlineBytes, int.MaxValue),
            });
        if (!validation.IsValid)
        {
            var failure = validation.Errors[0];
            error = $"typed raster input '{name}' is invalid ({failure.Code}): {failure.Message}";
            return false;
        }

        switch (descriptor)
        {
            case InlineRasterSourceDescriptor inline:
                input = GdalRasterInput.Inline(inline.Payload);
                return true;

            case ObjectStoreCogRasterSourceDescriptor cog
                when !string.IsNullOrWhiteSpace(cog.Content.ETag)
                    && cog.DeclaredDimensions is not null:
                try
                {
                    input = GdalRasterInput.Referenced(
                        GdalVsiPath.Build(cog.Provider, cog.StoreReference, cog.ObjectKey),
                        cog.Content.ETag,
                        cog.DeclaredDimensions);
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    error = $"typed raster input '{name}' cannot be opened: {ex.Message}";
                    return false;
                }

            case ObjectStoreCogRasterSourceDescriptor:
                error = string.IsNullOrWhiteSpace(descriptor.Content.ETag)
                    ? $"typed raster input '{name}' requires an ETag for a conditional worker read"
                    : $"typed raster input '{name}' requires bounded catalog dimensions before native execution";
                return false;

            default:
                error = $"typed raster input '{name}' source type is not supported by the GDAL worker";
                return false;
        }
    }

    /// <summary>
    /// Resolves the first canonical process id from the durable spec, mirroring the
    /// lean dispatch helper's resolution order.
    /// </summary>
    public static string? ResolveProcessId(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.TryGetValue(GdalWorkerParameterKeys.ProcessDefinitions, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            var first = raw
                .Split(
                    GdalWorkerParameterKeys.MetadataListSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        if (parameters.TryGetValue("protocolProcessId", out var protocolProcessId)
            && !string.IsNullOrWhiteSpace(protocolProcessId))
        {
            return protocolProcessId;
        }

        return null;
    }

    /// <summary>
    /// Reads a step-input string value, reporting presence based purely on whether
    /// the canonical key exists in the durable spec. A present-but-empty value
    /// (the submit path persists empty inputs as <c>string.Empty</c>) is returned
    /// as-is and reported as present — a legitimately-empty parameter (empty WHERE/SQL
    /// filter, empty name prefix, empty output-name override) must not be silently
    /// dropped. Callers that genuinely require a non-empty value are responsible for
    /// their own emptiness check on the returned value.
    /// </summary>
    public static bool TryGetInput(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value)
    {
        if (parameters.TryGetValue(GdalWorkerParameterKeys.StepInputPrefix + name, out var raw))
        {
            value = raw ?? "";
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Reads and decodes a required base64-encoded step input. Use the
    /// <see cref="TryGetBase64Input(IReadOnlyDictionary{string, string}, string, long, out byte[], out string)"/>
    /// overload to apply the worker's <c>MaxArtifactBytes</c> ceiling in the
    /// same call — the cross-cutting size guard belongs at the decoder so
    /// every executor enforces it uniformly.
    /// </summary>
    public static bool TryGetBase64Input(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";

        if (!TryGetInput(parameters, name, out var raw))
        {
            error = $"missing required input '{name}'";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            error = $"input '{name}' is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = $"input '{name}' decoded to zero bytes";
            return false;
        }

        if (!GdalUntrustedInputGuard.IsAdmissible(bytes, out var guardReason))
        {
            error = $"input '{name}' rejected: {guardReason}";
            bytes = [];
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads and decodes a required base64-encoded step input, rejecting any
    /// payload that exceeds <paramref name="maxBytes"/>. Centralizes the
    /// <c>MaxArtifactBytes</c> guard so every base64 input — primary source
    /// AND secondary inputs like clip boundaries or zones GeoJSON — is bounded
    /// by the same worker-wide ceiling without depending on each executor to
    /// remember to add an after-the-fact size check.
    /// </summary>
    public static bool TryGetBase64Input(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        long maxBytes,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";

        if (!TryGetInput(parameters, name, out var raw))
        {
            error = $"missing required input '{name}'";
            return false;
        }

        // Reject oversized payloads BEFORE decoding so the MaxArtifactBytes
        // ceiling caps the decode-time allocation rather than letting an
        // attacker materialize ~0.75x the (unbounded) base64 length first.
        // Base64 packs 4 encoded chars into 3 decoded bytes, so the decoded
        // size is at most (raw.Length / 4) * 3. Any whitespace/newlines the
        // encoder may have inserted only make the real decoded size smaller,
        // so this remains a valid (conservative) upper bound on the payload.
        // Use long arithmetic to avoid int overflow on huge inputs.
        if ((long)raw.Length / 4 * 3 > maxBytes)
        {
            error = $"input '{name}' size exceeds configured MaxArtifactBytes={maxBytes}";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            error = $"input '{name}' is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = $"input '{name}' decoded to zero bytes";
            bytes = [];
            return false;
        }

        // Refuse a content-sniffed VRT/XML indirection blob or an embedded /vsi
        // reference before it is ever materialized and opened by GDAL (#2765).
        if (!GdalUntrustedInputGuard.IsAdmissible(bytes, out var guardReason))
        {
            error = $"input '{name}' rejected: {guardReason}";
            bytes = [];
            return false;
        }

        // Belt-and-suspenders exact check: the upper-bound pre-check above can
        // admit a payload slightly over the cap when '=' padding shrinks the
        // real decoded length below the conservative bound. Cheap now that the
        // input is bounded.
        if (bytes.Length > maxBytes)
        {
            error = $"input '{name}' size {bytes.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}";
            bytes = [];
            return false;
        }

        return true;
    }
}

/// <summary>A bounded inline payload or a pinned GDAL-readable object-store path.</summary>
internal readonly record struct GdalRasterInput(
    byte[]? InlineBytes,
    string? ReferencedPath,
    string? ExpectedETag,
    RasterSourceDimensions? DeclaredDimensions)
{
    /// <summary>Whether the input is an inline payload that must be staged to scratch.</summary>
    public bool IsInline => InlineBytes is not null;

    /// <summary>Creates an inline input.</summary>
    public static GdalRasterInput Inline(byte[] bytes) => new(bytes, null, null, null);

    /// <summary>Creates a conditional object-store input.</summary>
    public static GdalRasterInput Referenced(
        string path,
        string expectedETag,
        RasterSourceDimensions declaredDimensions) =>
        new(null, path, expectedETag, declaredDimensions);

    /// <summary>
    /// Applies the same pre-GDAL dimension guard to inline headers and catalog-probed
    /// referenced dimensions. Referenced inputs fail closed when dimensions are absent.
    /// </summary>
    public bool TryAdmit(GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (InlineBytes is { } bytes)
        {
            return GdalRasterDimensionGuard.TryAdmit(bytes, options, out error);
        }

        if (DeclaredDimensions is not { } dimensions)
        {
            error = "referenced raster is missing bounded catalog dimensions";
            return false;
        }

        return GdalRasterDimensionGuard.IsWithinLimits(
            new GdalRasterDimensionGuard.RasterDimensions(
                dimensions.Width,
                dimensions.Height,
                dimensions.BandCount,
                dimensions.BitsPerSample),
            options,
            out error);
    }

    /// <summary>
    /// Returns the GDAL input path, writing only bounded inline content to scratch.
    /// Referenced objects are opened in place through VSI.
    /// </summary>
    public async Task<string> PreparePathAsync(
        string workspace,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (InlineBytes is { } bytes)
        {
            var path = Path.Join(workspace, fileName);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }

        return ReferencedPath
            ?? throw new InvalidOperationException("Raster input has neither inline bytes nor a referenced path.");
    }

    /// <summary>
    /// Adds a provider conditional-read header so mutation after submission fails closed.
    /// GDAL applies this header to VSI HEAD/range requests; a changed object returns 412.
    /// </summary>
    public void AddReadPin(List<string> arguments)
        => AddReadPin(arguments, ExpectedETag);

    /// <summary>Adds a provider conditional-read header for a pinned object ETag.</summary>
    public static void AddReadPin(List<string> arguments, string? expectedETag)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (string.IsNullOrWhiteSpace(expectedETag))
        {
            return;
        }

        arguments.Add("--config");
        arguments.Add("GDAL_HTTP_HEADERS");
        arguments.Add($"If-Match: {expectedETag}");
    }
}
