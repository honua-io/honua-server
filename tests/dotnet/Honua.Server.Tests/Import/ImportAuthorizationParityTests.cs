// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Honua.Server.Tests.Import;

/// <summary>Real HTTP transport parity for both adapters over the file-import mutation.</summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Mcp)]
public sealed class ImportAuthorizationParityTests : IAsyncLifetime
{
    private const string Issuer = "https://import-auth.test";
    private const string Audience = "import-auth-client";
    private const string SigningKey = "import-auth-matrix-signing-key-32!";
    private const string GeoJson =
        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-157.86,21.31]},"properties":{"name":"Honolulu"}}]}""";

    private readonly IFileImportService _importService;
    private readonly WebAppFixture _fixture;
    private readonly List<ImportRequest> _effects = [];
    private string _workspaceRole = null!;
    private string _adminReadKey = null!;
    private string _adminWriteKey = null!;
    private string _approvedUploadKey = null!;

    public ImportAuthorizationParityTests()
    {
        _importService = Substitute.For<IFileImportService>();
        _importService.Limits.Returns(ImportLimits.Default);
        _importService.DetectFormat(Arg.Any<string>()).Returns(SupportedFileFormat.GeoJson);
        _importService.GetSupportedExtensions().Returns([".geojson"]);
        _importService.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ImportRequest>(0);
                _effects.Add(request);
                return ImportResult.CreateSuccess(
                    request.TableName ?? "matrix_dataset",
                    SupportedFileFormat.GeoJson,
                    featureCount: 1,
                    detectedSrid: 4326,
                    physicalTableName: request.TableName ?? "matrix_dataset",
                    schema: request.TargetSchema ?? "public");
            });

        _fixture = new WebAppFixture()
            .ReplaceService<IFileImportService>(_importService)
            .ConfigureServices(services =>
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IMcpTool, IngestDatasetTool>()))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Authentication:ClientCertificates:Mode", "Optional");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", Issuer);
                builder.UseSetting("Oidc:Generic:ClientId", Audience);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var keyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        _adminReadKey = (await keyStore.CreateAsync(
            $"import-reader-{Guid.NewGuid():N}",
            ["admin:read"],
            DateTimeOffset.UtcNow.AddMinutes(15),
            "import-authorization-test",
            CancellationToken.None)).Key;
        _adminWriteKey = (await keyStore.CreateAsync(
            $"import-writer-{Guid.NewGuid():N}",
            ["admin:write"],
            DateTimeOffset.UtcNow.AddMinutes(15),
            "import-authorization-test",
            CancellationToken.None)).Key;
        _approvedUploadKey = (await keyStore.CreateAsync(
            $"approved-operation:import-upload-{Guid.NewGuid():N}",
            AdminApiKeyPermission.CreateApprovedOperationGrants("POST", "/api/v1/admin/import/upload", "public"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            "import-authorization-test",
            CancellationToken.None)).Key;

        _workspaceRole = $"import-workspace-create-{Guid.NewGuid():N}";
        var roleStore = _fixture.Services.GetRequiredService<IRoleStore>();
        _ = await roleStore.CreateRoleAsync(new RoleDefinition
        {
            Name = _workspaceRole,
            Permissions =
            [
                new PermissionGrant
                {
                    Service = "workspace",
                    Layer = "*",
                    Operation = "create",
                },
            ],
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    public async Task ImportAuthority_RealTransportTenCaseMatrix_MatchesRestAdminWriteContract()
    {
        var cases = (
            from surface in Enum.GetValues<ImportSurface>()
            from profile in Enum.GetValues<PrincipalProfile>()
            select new MatrixCase(surface, profile)).ToArray();

        cases.Should().HaveCount(10);
        cases.Count(testCase => testCase.Profile != PrincipalProfile.AdminWrite)
            .Should().Be(8);

        foreach (var testCase in cases)
        {
            _importService.ClearReceivedCalls();
            _effects.Clear();

            using var client = CreateClient(testCase.Profile);
            using var request = BuildRequest(testCase.Surface, testCase.Profile);
            using var response = await client.SendAsync(request);

            if (testCase.Profile == PrincipalProfile.AdminWrite)
            {
                await AssertAllowedAsync(response, testCase.Surface);
                await _importService.Received(1).ImportFileAsync(
                    Arg.Any<ImportRequest>(),
                    Arg.Any<CancellationToken>());
                _effects.Should().ContainSingle();
                _effects[0].OverwriteExisting.Should().Be(
                    testCase.Surface == ImportSurface.McpInline,
                    "MCP's forced overwrite and REST's caller option cannot change authority");
            }
            else
            {
                await AssertDeniedAsync(response, testCase.Surface, testCase.Profile);
                await _importService.DidNotReceive().ImportFileAsync(
                    Arg.Any<ImportRequest>(),
                    Arg.Any<CancellationToken>());
                await _importService.DidNotReceive().ImportFileAsync(
                    Arg.Any<ImportRequest>(),
                    Arg.Any<IProgress<ImportProgress>?>(),
                    Arg.Any<CancellationToken>());
                _effects.Should().BeEmpty(
                    $"{testCase.Surface}/{testCase.Profile} must have zero create or replace effects");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    public async Task BearerAdminGrant_ScopeCanOnlyNarrowBothImportTransports()
    {
        foreach (var surface in Enum.GetValues<ImportSurface>())
        {
            _importService.ClearReceivedCalls();
            _effects.Clear();
            using (var readScoped = CreateBearerClient(
                       roles: ["admin"],
                       scope: OperatorScopeCatalog.Read))
            using (var request = BuildRequest(surface, PrincipalProfile.AdminWrite))
            using (var denied = await readScoped.SendAsync(request))
            {
                await AssertDeniedAsync(denied, surface, PrincipalProfile.AdminRead);
                await _importService.DidNotReceive().ImportFileAsync(
                    Arg.Any<ImportRequest>(),
                    Arg.Any<CancellationToken>());
                _effects.Should().BeEmpty();
            }

            _importService.ClearReceivedCalls();
            _effects.Clear();
            using var createScoped = CreateBearerClient(
                roles: ["admin"],
                scope: OperatorScopeCatalog.Create);
            using var allowedRequest = BuildRequest(surface, PrincipalProfile.AdminWrite);
            using var allowed = await createScoped.SendAsync(allowedRequest);
            await AssertAllowedAsync(allowed, surface);
            await _importService.Received(1).ImportFileAsync(
                Arg.Any<ImportRequest>(),
                Arg.Any<CancellationToken>());
            _effects.Should().ContainSingle();
        }

        _importService.ClearReceivedCalls();
        _effects.Clear();
        using var approvedClient = CreateApiKeyClient(_approvedUploadKey);
        using var approvedRequest = BuildRestRequest(PrincipalProfile.AdminWrite);
        using var approvedResponse = await approvedClient.SendAsync(approvedRequest);

        await AssertAllowedAsync(approvedResponse, ImportSurface.RestUpload);
        await _importService.Received(1).ImportFileAsync(
            Arg.Any<ImportRequest>(),
            Arg.Any<CancellationToken>());
        await _importService.DidNotReceive().ImportFileAsync(
            Arg.Any<ImportRequest>(),
            Arg.Any<IProgress<ImportProgress>?>(),
            Arg.Any<CancellationToken>());
        _effects.Should().ContainSingle("the exact approved upload grant must survive the shared semantic gate");
    }

    private HttpClient CreateClient(PrincipalProfile profile) => profile switch
    {
        PrincipalProfile.Anonymous => _fixture.CreateClient(),
        PrincipalProfile.AuthenticatedNoGrant => CreateBearerClient(
            roles: [],
            scope: OperatorScopeCatalog.Create),
        PrincipalProfile.WorkspaceCreate => CreateBearerClient(
            roles: [_workspaceRole],
            scope: OperatorScopeCatalog.Create),
        PrincipalProfile.AdminRead => CreateApiKeyClient(_adminReadKey),
        PrincipalProfile.AdminWrite => CreateApiKeyClient(_adminWriteKey),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private HttpClient CreateApiKeyClient(string key) =>
        _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));

    private HttpClient CreateBearerClient(IReadOnlyList<string> roles, string? scope)
    {
        var token = CreateToken(roles, scope);
        return _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token));
    }

    private static string CreateToken(IReadOnlyList<string> roles, string? scope)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, $"import-principal-{Guid.NewGuid():N}"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("tenant_id", "default"),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (scope is not null)
        {
            claims.Add(new Claim(OperatorScopeCatalog.ScopeClaimType, scope));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage BuildRequest(
        ImportSurface surface,
        PrincipalProfile profile) =>
        surface switch
        {
            ImportSurface.RestUpload => BuildRestRequest(profile),
            ImportSurface.McpInline => BuildMcpRequest(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

    private static HttpRequestMessage BuildRestRequest(PrincipalProfile profile)
    {
        var payload = profile == PrincipalProfile.Anonymous
            ? GeoJson + new string(' ', 64 * 1024)
            : GeoJson;
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(payload, Encoding.UTF8, "application/geo+json"), "file", "matrix.geojson");
        content.Add(new StringContent("matrix_rest"), "TableName");
        content.Add(new StringContent("false"), "OverwriteExisting");
        return new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/import/upload")
        {
            Content = content,
        };
    }

    private static HttpRequestMessage BuildMcpRequest(PrincipalProfile profile)
    {
        object arguments = profile switch
        {
            PrincipalProfile.Anonymous => new
            {
                format = "geojson",
                data = GeoJson + new string(' ', 64 * 1024),
                datasetName = "matrix_mcp",
            },
            PrincipalProfile.WorkspaceCreate => new
            {
                format = "csv",
                data = "name,address\nHonua,123 Ocean Ave\n",
                datasetName = "matrix_mcp",
                addressColumn = "address",
            },
            _ => new
            {
                format = "geojson",
                data = GeoJson,
                datasetName = "matrix_mcp",
            },
        };
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = $"import-{profile}",
            method = "tools/call",
            @params = new
            {
                name = "honua_ingest_dataset",
                arguments,
            },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task AssertDeniedAsync(
        HttpResponseMessage response,
        ImportSurface surface,
        PrincipalProfile profile)
    {
        if (surface == ImportSurface.RestUpload)
        {
            response.StatusCode.Should().Be(
                profile == PrincipalProfile.Anonymous
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.Forbidden);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("result", out var result))
        {
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            result.GetProperty("structuredContent").GetProperty("code").GetString()
                .Should().Be(profile == PrincipalProfile.Anonymous ? "unauthenticated" : "permission_denied");
            return;
        }

        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32602);
        error.GetProperty("message").GetString().Should()
            .Contain("Unknown MCP tool").And.Contain("honua_ingest_dataset");
        error.GetProperty("data").GetProperty("code").GetString()
            .Should().Be("invalid_argument");
    }

    private static async Task AssertAllowedAsync(
        HttpResponseMessage response,
        ImportSurface surface)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        if (surface == ImportSurface.McpInline)
        {
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("result", out var result)
                .Should().BeTrue("the MCP allow envelope was {0}", body);
            result.GetProperty("isError").GetBoolean().Should().BeFalse();
        }
    }

    private enum ImportSurface
    {
        RestUpload,
        McpInline,
    }

    private enum PrincipalProfile
    {
        Anonymous,
        AuthenticatedNoGrant,
        WorkspaceCreate,
        AdminRead,
        AdminWrite,
    }

    private sealed record MatrixCase(
        ImportSurface Surface,
        PrincipalProfile Profile);
}
