// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;

namespace Honua.TestKit.Providers;

/// <summary>
/// Common in-memory app-configuration entries shared by the standalone provider-smoke
/// fixtures (<see cref="DuckDbProviderWebAppFixture"/>, <see cref="MySqlProviderWebAppFixture"/>).
/// Mirrors the provider-neutral subset of
/// <c>Honua.TestKit.Mixins.WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary</c> —
/// duplicated rather than shared because that mixin's dictionary hard-codes
/// <c>ConnectionStrings:*</c> to a Postgres connection string these fixtures never have.
/// </summary>
internal static class ProviderSmokeHostConfiguration
{
    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private const string TestEncryptionMasterKey =
        "test-master-key-that-is-at-least-32-characters-long-for-security";
    private const string TestEncryptionSalt =
        "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=";

    /// <summary>
    /// Applies the merged provider-smoke settings to <paramref name="builder"/> via
    /// <see cref="IWebHostBuilder.UseSetting(string, string?)"/> rather than
    /// <c>ConfigureAppConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// This distinction matters: <c>Program.cs</c> reads several of these keys (notably
    /// <c>DataSource:Provider</c>, <c>HONUA_REGISTER_TEST_INFRASTRUCTURE</c>, and
    /// <c>HONUA_SKIP_MIGRATIONS</c>) directly off <c>builder.Configuration</c> as top-level
    /// statements executed while the <c>WebApplicationBuilder</c> is still being assembled —
    /// before <c>WebApplicationFactory</c>'s <c>ConfigureAppConfiguration</c> callback has run.
    /// <c>UseSetting</c> values, by contrast, are folded into the host's configuration at
    /// construction time (the same mechanism <c>UseEnvironment</c>/<c>HONUA_DEV_AUTH</c> rely
    /// on in <c>WebAppFixturePostgresWiringMixin.ApplyCommonHostSettings</c>), so Program.cs's
    /// early reads see them. A standalone (non-<c>WebAppFixture</c>) provider host that needs
    /// the composition root to read a non-default <c>DataSource:Provider</c> must use this path,
    /// not <c>ConfigureAppConfiguration</c>.
    /// </remarks>
    internal static void ApplySettings(IWebHostBuilder builder, IDictionary<string, string?> providerSettings)
    {
        foreach (var (key, value) in Build(providerSettings))
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>
    /// Builds the merged settings dictionary for a standalone (non-Postgres-primary)
    /// provider-smoke host, merging in the caller's provider-specific settings.
    /// </summary>
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
            // ArcGisRest/Oracle) dormant rather than erroring on missing config
            // (honua-server "secondary-provider-dormant-startup" contract).
            ["SqlServer:Enabled"] = "false",
            ["ArcGisRest:Enabled"] = "false",
            ["Oracle:Enabled"] = "false",
        };

        foreach (var (key, value) in providerSettings)
        {
            settings[key] = value;
        }

        return settings;
    }
}
