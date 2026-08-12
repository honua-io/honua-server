// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

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
    /// <param name="s3Options">
    /// Execution-owned S3 endpoint settings projected only for trusted remote-VSI
    /// invocations. The durable job descriptor remains limited to bucket and key.
    /// </param>
    /// <param name="azureOptions">Execution-owned Azure Blob connection settings.</param>
    /// <param name="inputReferencesS3Vsi">Whether the arguments contain a trusted <c>/vsis3</c> path.</param>
    /// <param name="inputReferencesAzureVsi">Whether the arguments contain a trusted <c>/vsiaz</c> path.</param>
    /// <param name="environmentVariableReader">
    /// Optional environment seam for tests. Production uses <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </param>
    /// <returns>An ordered map of environment variable name to value.</returns>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        GdalHardeningOptions options,
        bool inputReferencesRemoteVsi,
        AwsS3Options? s3Options = null,
        AzureBlobOptions? azureOptions = null,
        bool inputReferencesS3Vsi = false,
        bool inputReferencesAzureVsi = false,
        Func<string, string?>? environmentVariableReader = null)
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

        if (inputReferencesS3Vsi && s3Options is not null)
        {
            AddS3Environment(env, s3Options);
            if (string.IsNullOrWhiteSpace(s3Options.AccessKeyId)
                && string.IsNullOrWhiteSpace(s3Options.SecretAccessKey))
            {
                AddAmbientS3Credentials(env, environmentVariableReader ?? Environment.GetEnvironmentVariable);
            }
        }

        if (inputReferencesAzureVsi && azureOptions is not null)
        {
            AddAzureEnvironment(env, azureOptions);
        }

        return env;
    }

    private static void AddAmbientS3Credentials(
        Dictionary<string, string> env,
        Func<string, string?> environmentVariableReader)
    {
        foreach (var name in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
        {
            var value = environmentVariableReader(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                env[name] = value;
            }
        }
    }

    private static void AddS3Environment(Dictionary<string, string> env, AwsS3Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.AccessKeyId))
        {
            env["AWS_ACCESS_KEY_ID"] = options.AccessKeyId;
        }

        if (!string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            env["AWS_SECRET_ACCESS_KEY"] = options.SecretAccessKey;
        }

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            env["AWS_REGION"] = options.Region.Trim();
        }

        if (options.ForcePathStyle)
        {
            env["AWS_VIRTUAL_HOSTING"] = "FALSE";
        }

        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            return;
        }

        var serviceUrl = options.ServiceUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "FileStorage:AwsS3:ServiceUrl must be an absolute HTTP or HTTPS URL for GDAL /vsis3 access.");
        }

        // The worker image pins GDAL >= 3.11, where AWS_S3_ENDPOINT accepts a full
        // URL. Keeping the scheme preserves the registered endpoint's transport;
        // AWS_HTTPS is also set for clarity and compatibility with older dev CLIs.
        env["AWS_S3_ENDPOINT"] = serviceUrl;
        env["AWS_HTTPS"] = endpoint.Scheme == Uri.UriSchemeHttps ? "YES" : "NO";
    }

    private static void AddAzureEnvironment(
        Dictionary<string, string> env,
        AzureBlobOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            env["AZURE_STORAGE_CONNECTION_STRING"] = options.ConnectionString.Trim();
        }
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

    /// <summary>Reports whether any argument references an S3 VSI path.</summary>
    public static bool ArgumentsReferenceS3Vsi(IReadOnlyList<string> arguments)
        => ArgumentsReferenceVsiPrefix(arguments, "/vsis3/");

    /// <summary>Reports whether any argument references an Azure Blob VSI path.</summary>
    public static bool ArgumentsReferenceAzureVsi(IReadOnlyList<string> arguments)
        => ArgumentsReferenceVsiPrefix(arguments, "/vsiaz/");

    private static bool ArgumentsReferenceVsiPrefix(
        IReadOnlyList<string> arguments,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(arg =>
            arg is not null && arg.Contains(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Formats the environment as <c>docker run -e KEY</c> argument pairs, in a
    /// stable order, for the container-exec runner. Values are supplied to the
    /// container-runtime process environment so credentials never enter argv.
    /// </summary>
    public static IReadOnlyList<string> ToDockerEnvArguments(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var args = new List<string>(environment.Count * 2);
        foreach (var kvp in environment)
        {
            args.Add("-e");
            args.Add(kvp.Key);
        }

        return args;
    }
}
