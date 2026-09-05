// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed partial class GeoprocessingOutputStoreAttestationReporter(
    IOptions<GeoprocessingOutputStagingOptions> options,
    ILogger<GeoprocessingOutputStoreAttestationReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var attestation = GeoprocessingOutputStoreAttestationValidator.Validate(options.Value);
        LogAttestation(logger, attestation.Provider, attestation.StoreReference,
            attestation.ConfigurationDigest, attestation.PersistenceClass, attestation.BackupIdentity);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 8900, Level = LogLevel.Information,
        Message = "GP output store attestation: provider={Provider} storeReference={StoreReference} configurationDigest={ConfigurationDigest} persistenceClass={PersistenceClass} backupIdentity={BackupIdentity}")]
    private static partial void LogAttestation(ILogger logger, string provider, string storeReference,
        string configurationDigest, string persistenceClass, string backupIdentity);
}
