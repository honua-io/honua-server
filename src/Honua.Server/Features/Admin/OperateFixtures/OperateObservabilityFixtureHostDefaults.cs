// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.OperateFixtures;

// Fixture connection defaults apply only in Development or Test when the fixture is enabled and the key is unset, and they never stand in for a production secret.
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
