// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Builds the restrictive GDAL environment every GDAL/OGR CLI subprocess inherits
/// (#2765). Pure and deterministic so the exact variable set can be asserted
/// offline; both the in-process (<see cref="ProcessGdalCommandRunner"/>) and
/// container-exec (<see cref="DockerGdalCommandRunner"/>) runners apply it so no
/// invocation escapes the driver-skip / remote-VSI-disable policy.
/// </summary>
internal static class GdalRuntimeHardening
{
    /// <summary>Sentinel extension gated into the <c>/vsicurl</c>-family allow-list to block it.</summary>
    private const string BlockedCurlExtension = ".honua-vsi-blocked";

    /// <summary>
    /// Builds the GDAL environment variables to set on a subprocess.
    /// </summary>
    /// <param name="options">The configured hardening policy.</param>
    /// <param name="inputReferencesRemoteVsi">
    /// <see langword="true"/> when the invocation's argument vector legitimately
    /// references a <c>/vsi</c> path (the trusted cloud-coverage reader). Such an
    /// invocation keeps remote VSI enabled; a pure local-scratch invocation (every
    /// untrusted-blob executor) gets the remote handlers neutralized.
    /// </param>
    /// <returns>An ordered map of environment variable name to value.</returns>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        GdalHardeningOptions options,
        bool inputReferencesRemoteVsi)
    {
        ArgumentNullException.ThrowIfNull(options);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Exclude the indirection / network / archive drivers so a content-sniffed
            // VRT / OGR VRT / WMS / … is never registered and cannot open, no matter
            // what bytes an untrusted input claims to be.
            ["GDAL_SKIP"] = string.Join(
                ' ',
                options.SkipDrivers.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim())),

            // Never negotiate around an invalid TLS certificate.
            ["GDAL_HTTP_UNSAFESSL"] = "NO",
        };

        if (options.DisableRemoteVsiForLocalInputs && !inputReferencesRemoteVsi)
        {
            // Local-scratch-only invocation (every untrusted-blob path): neutralize the
            // remote virtual-filesystem handlers. Gating the /vsicurl-family allow-list
            // to an extension no real input carries blocks arbitrary-URL range reads;
            // suppressing directory pre-scan and HTTP retries closes the residual reach.
            env["CPL_VSIL_CURL_ALLOWED_EXTENSIONS"] = BlockedCurlExtension;
            env["GDAL_HTTP_MAX_RETRY"] = "0";
            env["GDAL_HTTP_CONNECTTIMEOUT"] = "2";
            env["GDAL_DISABLE_READDIR_ON_OPEN"] = "EMPTY_DIR";
        }

        return env;
    }

    /// <summary>
    /// Reports whether any argument references a GDAL virtual-filesystem path
    /// (<c>/vsi…</c>). Used by the runners to decide whether an invocation is the
    /// trusted cloud-coverage reader (keep remote VSI) or a local-scratch-only op
    /// (neutralize remote VSI).
    /// </summary>
    public static bool ArgumentsReferenceVsi(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(arg => arg is not null && arg.Contains("/vsi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Formats the environment as <c>docker run -e KEY=VALUE</c> argument pairs, in a
    /// stable order, for the container-exec runner.
    /// </summary>
    public static IReadOnlyList<string> ToDockerEnvArguments(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var args = new List<string>(environment.Count * 2);
        foreach (var kvp in environment)
        {
            args.Add("-e");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"{kvp.Key}={kvp.Value}"));
        }

        return args;
    }
}
