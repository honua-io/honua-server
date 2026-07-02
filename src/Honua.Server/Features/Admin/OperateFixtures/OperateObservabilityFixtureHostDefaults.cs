// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.OperateFixtures;

/// <summary>
/// Supplies the deterministic Development/Test-only configuration defaults a standalone Operate
/// observability fixture host needs to be self-sufficient. The honua-console live lane boots the
/// server image via Testcontainers with only a database connection string and the fixture flags;
/// it does not supply the connection-encryption master key that the Postgres secure-connection
/// provider (and therefore the audit-log/connection-provider request path) requires. Without a
/// key every request — including <c>/healthz/live</c> and the fixture seed endpoint — fails with
/// "Master key not configured." (honua-server#2350).
/// </summary>
/// <remarks>
/// These defaults are applied only when the fixture is enabled in the Development or Test
/// environment (the fixture validator forbids Production), and only for keys that are not already
/// configured, so an explicit operator/WebAppFixture value always wins. They protect throwaway
/// fixture connection strings in an ephemeral test container, never production secrets.
/// </remarks>
internal static class OperateObservabilityFixtureHostDefaults
{
    internal const string MasterKeyConfigurationPath = "Security:ConnectionEncryption:MasterKey";
    internal const string SaltConfigurationPath = "Security:ConnectionEncryption:Salt";

    // 49 characters, satisfying the 32-character minimum enforced by ConnectionEncryptionService.
    internal const string DefaultConnectionEncryptionMasterKey =
        "honua-operate-fixture-development-master-key-0001";

    // Base64 of "honua-operate-fixture-development-salt"; any valid base64 salt round-trips
    // within a single process, which is all the ephemeral fixture host needs.
    internal const string DefaultConnectionEncryptionSalt =
        "aG9udWEtb3BlcmF0ZS1maXh0dXJlLWRldmVsb3BtZW50LXNhbHQ=";

    /// <summary>
    /// Returns the fixture host defaults that are not already present in <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The current host configuration.</param>
    /// <returns>
    /// A map of configuration keys to default values for every default not already configured. The
    /// map is empty when the operator (or WebAppFixture) has supplied all of them.
    /// </returns>
    public static IReadOnlyDictionary<string, string?> CreateMissingDefaults(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddIfMissing(configuration, defaults, MasterKeyConfigurationPath, DefaultConnectionEncryptionMasterKey);
        AddIfMissing(configuration, defaults, SaltConfigurationPath, DefaultConnectionEncryptionSalt);
        return defaults;
    }

    private static void AddIfMissing(
        IConfiguration configuration,
        Dictionary<string, string?> defaults,
        string key,
        string value)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            defaults[key] = value;
        }
    }
}
