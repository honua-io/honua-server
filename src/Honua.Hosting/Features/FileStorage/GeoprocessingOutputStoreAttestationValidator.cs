// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal static class GeoprocessingOutputStoreAttestationValidator
{
    internal const string FailureMessage = "Geoprocessing:OutputStaging persistence attestation is missing or mismatched. Provision the shared volume marker and identical configuration digest, persistence class and backup inventory on every server and worker.";

    internal static GeoprocessingOutputStoreAttestation Validate(GeoprocessingOutputStagingOptions options)
    {
        try
        {
            var expected = GeoprocessingOutputStoreAttestation.Create(options);
            if (string.IsNullOrWhiteSpace(options.LocalRootPath)
                || !Path.IsPathFullyQualified(options.LocalRootPath)
                || options.ConfigurationDigest != expected.ConfigurationDigest)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            var marker = Path.Join(options.LocalRootPath, GeoprocessingOutputStoreAttestation.FileName);
            using var stream = File.OpenRead(marker);
            if (stream.Length > 4096
                || JsonSerializer.Deserialize(stream, OutputStoreAttestationJsonContext.Default.GeoprocessingOutputStoreAttestation) != expected)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            return expected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Never include mount paths, marker content or provider exception details.
            throw new InvalidOperationException(FailureMessage);
        }
    }

    internal static bool IsValid(GeoprocessingOutputStagingOptions options)
    {
        try
        {
            Validate(options);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class GeoprocessingOutputStoreHealthCheck(IOptions<GeoprocessingOutputStagingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var attestation = GeoprocessingOutputStoreAttestationValidator.Validate(options.Value);
            return Task.FromResult(HealthCheckResult.Healthy("Referenced output store attested.", new Dictionary<string, object>
            {
                ["provider"] = attestation.Provider,
                ["storeReference"] = attestation.StoreReference,
                ["configurationDigest"] = attestation.ConfigurationDigest,
                ["persistenceClass"] = attestation.PersistenceClass,
                ["backupIdentity"] = attestation.BackupIdentity,
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or OptionsValidationException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(GeoprocessingOutputStoreAttestationValidator.FailureMessage));
        }
    }
}

[JsonSerializable(typeof(GeoprocessingOutputStoreAttestation))]
internal sealed partial class OutputStoreAttestationJsonContext : JsonSerializerContext;
