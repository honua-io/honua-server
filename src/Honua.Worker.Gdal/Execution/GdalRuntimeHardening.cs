// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    // Only an invocation whose arguments include an object-store path declared through
    // GdalRemoteInputScope keeps remote VSI; every other one, including every
    // untrusted-blob executor, gets the remote handlers neutralized. The S3 and Azure
    // settings are execution-owned and projected only for those trusted invocations;
    // the durable job descriptor stays limited to bucket and key.
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
            ["GDAL_SKIP"] = string.Join(' ', EffectiveSkipDrivers(options)),

            // Never negotiate around an invalid TLS certificate.
            ["GDAL_HTTP_UNSAFESSL"] = "NO",

            // The GML driver otherwise fetches the xsi:schemaLocation an input
            // names, which turns an uploaded file into an outbound request.
            ["GML_DOWNLOAD_SCHEMA"] = "NO",
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

    /// <summary>
    /// Driver short names written to <c>GDAL_SKIP</c>. Blank entries are dropped
    /// and the rest are trimmed, in configured order. Output-driver admission
    /// must use this sequence so a padded token cannot disagree with the
    /// subprocess environment.
    /// </summary>
    /// <param name="options">The configured hardening policy.</param>
    /// <returns>The effective skip list, possibly empty.</returns>
    public static IReadOnlyList<string> EffectiveSkipDrivers(GdalHardeningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var skipped = new List<string>();
        foreach (var driver in options.SkipDrivers)
        {
            if (string.IsNullOrWhiteSpace(driver))
            {
                continue;
            }

            skipped.Add(driver.Trim());
        }

        return skipped;
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
