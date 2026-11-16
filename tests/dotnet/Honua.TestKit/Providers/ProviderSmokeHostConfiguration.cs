// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;

namespace Honua.TestKit.Providers;

internal static class ProviderSmokeHostConfiguration
{
    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private const string TestEncryptionMasterKey =
        "test-master-key-that-is-at-least-32-characters-long-for-security";
    private const string TestEncryptionSalt =
        "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=";

    internal static void ApplySettings(IWebHostBuilder builder, IDictionary<string, string?> providerSettings)
    {
        foreach (var (key, value) in Build(providerSettings))
        {
            builder.UseSetting(key, value);
        }
    }

    private static Dictionary<string, string?> Build(IDictionary<string, string?> providerSettings)
    {
        var attachmentsPath = Path.Join(Directory.GetCurrentDirectory(), "tmp", "attachments");
        var settings = new Dictionary<string, string?>
        {
            ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
            ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
            ["HONUA_SKIP_MIGRATIONS"] = "true",
            // Program.cs's TestInfrastructureRegistrationPolicy skips
            // InfrastructureCompositionRoot.RegisterInfrastructureServices in the Test
            // environment by default, on the assumption that a WebAppFixture-style host
            // wires its own providers directly via ConfigureTestServices. These standalone
            // DuckDB/MySql fixtures have no such fixture — they rely on the ordinary
            // DataSource:Provider composition root switch reading the DuckDB/MySql config
            // supplied below — so they must opt back in explicitly.
            ["HONUA_REGISTER_TEST_INFRASTRUCTURE"] = "true",
            ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
            ["Limits:Connections:RequestTimeout"] = "00:05:00",
            ["Limits:Query:QueryTimeout"] = "00:02:00",
            ["FileStorage:Provider"] = "Local",
            ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
            ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
            ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt,
            ["Observability:OpsHealthRollup:Enabled"] = "false",
            // No secondary-provider connections are configured for these standalone
            // fixtures; keep the always-registered secondary providers (SqlServer/
            // GeoServicesRest/Oracle) dormant rather than erroring on missing config
            // (honua-server "secondary-provider-dormant-startup" contract).
            ["SqlServer:Enabled"] = "false",
            ["GeoServicesRest:Enabled"] = "false",
            ["Oracle:Enabled"] = "false",
        };

        foreach (var (key, value) in providerSettings)
        {
            settings[key] = value;
        }

        return settings;
    }
}
