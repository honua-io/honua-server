// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration coverage for client-certificate trust profile admin endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ClientCertificateAdminEndpointsTests : IDisposable
{
    private const string AdminPassword = "client-cert-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ClientCertificateAdminEndpointsTests()
    {
        _factory = CreateFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/security/client-certificates/profiles")]
    public async Task ListProfiles_WithAdminAuth_ReturnsConfiguredProfiles()
    {
        var response = await _client.GetAsync("/api/v1/admin/security/client-certificates/profiles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles")]
    public async Task CreateProfile_ValidRequest_ReturnsCreated()
    {
        var response = await CreateProfileAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("prod", data.GetProperty("environmentId").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/security/client-certificates/profiles/{profileId}")]
    public async Task GetProfile_ExistingProfile_ReturnsProfile()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await _client.GetAsync($"/api/v1/admin/security/client-certificates/profiles/{profileId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal(profileId, data.GetProperty("profileId").GetString());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/security/client-certificates/profiles/{profileId}")]
    public async Task UpdateProfile_ExistingProfile_ReturnsUpdatedRevision()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var request = CreateProfileRequest(
            profileId,
            displayName: "Updated profile",
            rotationGracePeriodDays: 14);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("Updated profile", data.GetProperty("displayName").GetString());
        Assert.True(data.GetProperty("revision").GetInt64() > 1);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}")]
    public async Task DisableProfile_ExistingProfile_ReturnsDisabledProfile()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await _client.DeleteAsync($"/api/v1/admin/security/client-certificates/profiles/{profileId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.False(data.GetProperty("enabled").GetBoolean());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings")]
    public async Task ListMappings_ExistingProfile_ReturnsMappings()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await _client.GetAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings")]
    public async Task CreateMapping_ValidRequest_ReturnsCreated()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await CreateMappingAsync(profileId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("native-prod-admin", data.GetProperty("principalId").GetString());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}")]
    public async Task UpdateMapping_ExistingMapping_ReturnsUpdatedMapping()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var mappingId = await CreateMappingAndReadIdAsync(profileId);
        var request = CreateMappingRequest(mappingId, displayName: "Updated mapping");

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("Updated mapping", data.GetProperty("displayName").GetString());
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}")]
    public async Task DisableMapping_ExistingMapping_ReturnsDisabledMapping()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var mappingId = await CreateMappingAndReadIdAsync(profileId);

        var response = await _client.DeleteAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{mappingId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.False(data.GetProperty("enabled").GetBoolean());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations")]
    public async Task ListRevocations_ExistingProfile_ReturnsRevocations()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await _client.GetAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations")]
    public async Task AddRevocation_ValidRequest_ReturnsCreated()
    {
        var profileId = await CreateProfileAndReadIdAsync();

        var response = await AddRevocationAsync(profileId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("rotation", data.GetProperty("reason").GetString());
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{revocationId}")]
    public async Task RemoveRevocation_ExistingRevocation_ReturnsOk()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var revocationId = await AddRevocationAndReadIdAsync(profileId);

        var response = await _client.DeleteAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{revocationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/validate")]
    public async Task ValidateCertificate_WithMappedCertificate_ReturnsValidResult()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", "spiffe://honua/prod/admin");
        var anchorPem = PemEncoding.WriteString("CERTIFICATE", certificate.RawData);
        var profileId = await CreateProfileAndReadIdAsync(certificate.Issuer, anchorPem);
        await CreateMappingAsync(profileId);
        var request = new ValidateClientCertificateRequest
        {
            Certificate = anchorPem,
            Encoding = "pem",
            ProfileId = profileId
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/validate",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.True(data.GetProperty("valid").GetBoolean());
        Assert.Equal("success", data.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles")]
    public async Task CreateProfile_WithExplicitNullArrays_ReturnsBadRequestWithoutNullReferenceException()
    {
        var profileId = $"profile-{Guid.NewGuid():N}";
        var payload = new
        {
            profileId,
            environmentId = "prod",
            displayName = "Null-arrays profile",
            enabled = true,
            acceptedIssuerSubjects = (string[]?)null,
            acceptedIssuerThumbprints = (string[]?)null,
            customTrustAnchorCertificates = (string[]?)null,
            allowedSanTypes = (string[]?)null,
            requireClientAuthenticationEku = true,
            chainRevocationMode = "NoCheck",
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/profiles",
            payload,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("issuer", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings")]
    public async Task CreateMapping_WithBlankMatchValue_ReturnsBadRequest()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var request = new UpsertClientCertificatePrincipalMappingRequest
        {
            MappingId = $"mapping-{Guid.NewGuid():N}",
            MatchType = "sanUri",
            MatchValue = "   ",
            PrincipalId = "native-prod-admin",
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MatchValue", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings")]
    public async Task CreateMapping_WithBlankPrincipalId_ReturnsBadRequest()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var request = new UpsertClientCertificatePrincipalMappingRequest
        {
            MappingId = $"mapping-{Guid.NewGuid():N}",
            MatchType = "sanUri",
            MatchValue = "spiffe://honua/prod/admin",
            PrincipalId = "",
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PrincipalId", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles/{profileId}/mappings")]
    public async Task CreateMapping_WithInvertedValidityWindow_ReturnsBadRequest()
    {
        var profileId = await CreateProfileAndReadIdAsync();
        var request = new UpsertClientCertificatePrincipalMappingRequest
        {
            MappingId = $"mapping-{Guid.NewGuid():N}",
            MatchType = "sanUri",
            MatchValue = "spiffe://honua/prod/admin",
            PrincipalId = "native-prod-admin",
            NotBefore = DateTimeOffset.UtcNow.AddDays(10),
            NotAfter = DateTimeOffset.UtcNow.AddDays(1),
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NotBefore", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/validate")]
    public async Task ValidateCertificate_WithBlankCertificate_ReturnsBadRequest()
    {
        var request = new ValidateClientCertificateRequest
        {
            Certificate = "   ",
            Encoding = "pem",
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/validate",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Certificate", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles")]
    public async Task CreateProfile_WithAcceptedIssuerSubjectsButNoChainTrust_ReturnsBadRequest()
    {
        var profileId = $"profile-{Guid.NewGuid():N}";
        var request = new UpsertClientCertificateTrustProfileRequest
        {
            ProfileId = profileId,
            EnvironmentId = "prod",
            DisplayName = "Forgeable subject-only profile",
            AcceptedIssuerSubjects = ["CN=Honua Native Prod"],
            AllowedSanTypes = ["sanUri"],
            RequireClientAuthenticationEku = true,
            RequireChainTrust = false,
            ChainRevocationMode = "NoCheck",
            ExpirationWarningThresholdDays = 15,
            RotationGracePeriodDays = 7,
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/profiles",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AcceptedIssuerSubjects", body, StringComparison.Ordinal);
        Assert.Contains("RequireChainTrust", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/security/client-certificates/profiles")]
    public async Task CreateProfile_WithMalformedCustomTrustAnchor_ReturnsBadRequest()
    {
        var profileId = $"profile-{Guid.NewGuid():N}";
        var request = new UpsertClientCertificateTrustProfileRequest
        {
            ProfileId = profileId,
            EnvironmentId = "prod",
            DisplayName = "Bad anchor profile",
            CustomTrustAnchorCertificates = ["not-a-pem-or-base64-cert"],
            AllowedSanTypes = ["sanUri"],
            RequireClientAuthenticationEku = true,
            RequireChainTrust = true,
            ChainRevocationMode = "NoCheck",
            ExpirationWarningThresholdDays = 15,
            RotationGracePeriodDays = 7,
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/profiles",
            request,
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CustomTrustAnchorCertificates", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/version")]
    public async Task RequiredForAdmin_WithApiKeyButMissingCertificate_ReturnsMachineReadableProblem()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Authentication:ClientCertificates:Mode"] = "RequiredForAdmin",
            ["Authentication:ClientCertificates:EnvironmentId"] = "prod",
            ["Authentication:ClientCertificates:TrustProfiles:0:ProfileId"] = "prod-native",
            ["Authentication:ClientCertificates:TrustProfiles:0:EnvironmentId"] = "prod",
            ["Authentication:ClientCertificates:TrustProfiles:0:AcceptedIssuerSubjects:0"] = "CN=Honua Native Prod",
            ["Authentication:ClientCertificates:TrustProfiles:0:RequireChainTrust"] = "true",
            ["Authentication:ClientCertificates:TrustProfiles:0:PrincipalMappings:0:MappingId"] = "prod-admin",
            ["Authentication:ClientCertificates:TrustProfiles:0:PrincipalMappings:0:MatchType"] = "SanUri",
            ["Authentication:ClientCertificates:TrustProfiles:0:PrincipalMappings:0:MatchValue"] = "spiffe://honua/prod/admin",
            ["Authentication:ClientCertificates:TrustProfiles:0:PrincipalMappings:0:PrincipalId"] = "native-prod-admin",
            ["Authentication:ClientCertificates:TrustProfiles:0:PrincipalMappings:0:Roles:0"] = "admin"
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var response = await client.GetAsync("/api/v1/admin/version");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("client_certificate_missing", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://honua.io/problems/security/client-certificate-missing", json.RootElement.GetProperty("type").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? extra = null)
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["HONUA_DEV_AUTH"] = "false",
                    ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                    ["Authentication:ClientCertificates:Mode"] = "Optional",
                    ["Authentication:ClientCertificates:EnvironmentId"] = "prod"
                };

                if (extra is not null)
                {
                    foreach (var pair in extra)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }

                configBuilder.AddInMemoryCollection(values);
            });
        });

    private async Task<HttpResponseMessage> CreateProfileAsync(
        string issuer = "CN=Honua Native Prod",
        string? anchorPem = null)
    {
        var profileId = $"profile-{Guid.NewGuid():N}";
        return await _client.PostAsJsonAsync(
            "/api/v1/admin/security/client-certificates/profiles",
            CreateProfileRequest(profileId, issuer, anchorPem: anchorPem),
            _jsonOptions);
    }

    private async Task<string> CreateProfileAndReadIdAsync(
        string issuer = "CN=Honua Native Prod",
        string? anchorPem = null)
    {
        var response = await CreateProfileAsync(issuer, anchorPem);
        var data = await ReadDataAsync(response);
        return data.GetProperty("profileId").GetString()!;
    }

    private static UpsertClientCertificateTrustProfileRequest CreateProfileRequest(
        string profileId,
        string issuer = "CN=Honua Native Prod",
        string displayName = "Production native operators",
        int rotationGracePeriodDays = 7,
        string? anchorPem = null)
        => new()
        {
            ProfileId = profileId,
            EnvironmentId = "prod",
            DisplayName = displayName,
            AcceptedIssuerSubjects = [issuer],
            CustomTrustAnchorCertificates = anchorPem is null ? [] : [anchorPem],
            AllowedSanTypes = ["sanUri"],
            RequireClientAuthenticationEku = true,
            RequireChainTrust = true,
            ExpirationWarningThresholdDays = 15,
            RotationGracePeriodDays = rotationGracePeriodDays
        };

    private async Task<HttpResponseMessage> CreateMappingAsync(string profileId)
        => await _client.PostAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings",
            CreateMappingRequest($"mapping-{Guid.NewGuid():N}"),
            _jsonOptions);

    private async Task<string> CreateMappingAndReadIdAsync(string profileId)
    {
        var response = await CreateMappingAsync(profileId);
        var data = await ReadDataAsync(response);
        return data.GetProperty("mappingId").GetString()!;
    }

    private static UpsertClientCertificatePrincipalMappingRequest CreateMappingRequest(
        string mappingId,
        string displayName = "Native prod admin")
        => new()
        {
            MappingId = mappingId,
            MatchType = "sanUri",
            MatchValue = "spiffe://honua/prod/admin",
            PrincipalId = "native-prod-admin",
            DisplayName = displayName,
            Roles = ["admin", "operator"],
            TenantId = "tenant-prod",
            EnvironmentScopes = ["prod"]
        };

    private async Task<HttpResponseMessage> AddRevocationAsync(string profileId)
        => await _client.PostAsJsonAsync(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations",
            new AddClientCertificateRevocationRequest
            {
                RevocationId = $"revocation-{Guid.NewGuid():N}",
                FingerprintSha256 = new string('A', 64),
                Reason = "rotation"
            },
            _jsonOptions);

    private async Task<string> AddRevocationAndReadIdAsync(string profileId)
    {
        var response = await AddRevocationAsync(profileId);
        var data = await ReadDataAsync(response);
        return data.GetProperty("revocationId").GetString()!;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static X509Certificate2 CreateCertificate(string subject, string uri)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
            critical: false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddUri(new Uri(uri));
        request.CertificateExtensions.Add(sanBuilder.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
    }
}
