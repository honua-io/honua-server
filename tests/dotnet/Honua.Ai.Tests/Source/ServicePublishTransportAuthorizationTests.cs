// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Real HTTP transport parity between the two hand-authored MCP publish tools
/// and the REST <c>service.publish</c> submit endpoint.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Mcp, TestProtocols.Admin)]
public sealed class ServicePublishTransportAuthorizationTests : IAsyncLifetime
{
    private const string Issuer = "https://publish-auth.test";
    private const string Audience = "publish-auth-client";
    private const string SigningKey = "publish-auth-matrix-signing-key-32!";
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";

    private static readonly bool[] ApprovalDecisions = [false, true];
    private readonly ILayerPublishingService _publishing;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IGeoprocessingJobService _jobService;
    private readonly MutableApprovalEvaluator _approvalEvaluator = new();
    private readonly WebAppFixture _fixture;
    private string _adminReadKey = null!;
    private string _adminWriteKey = null!;

    public ServicePublishTransportAuthorizationTests()
    {
        _publishing = Substitute.For<ILayerPublishingService>();
        _publishing.PublishLayerAsync(
                Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 7,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default",
            });
        _publishing.ValidateTableForPublishAsync(
                Arg.Any<string>(),
                Arg.Any<TablePublishValidationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default",
            });

        _graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        _graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
            new MetadataV2GraphSnapshot(
                new MetadataV2Graph { Revision = 42 },
                "\"publish-matrix-etag\"",
                DateTimeOffset.UtcNow));

        _jobService = Substitute.For<IGeoprocessingJobService>();
        _jobService.GetJobResultsAsync(
                Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                "publish-matrix-result",
                new ResultSummary { Title = "publish matrix" },
                [new ArtifactRef
                {
                    ArtifactId = "publish-matrix-artifact",
                    Kind = ArtifactKind.Table,
                    Label = "Parcels",
                    Uri = "honua://analysis/artifacts/publish-matrix-artifact",
                    ContentType = "application/json",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = ConnectionId,
                        ["schema"] = "public",
                        ["table"] = "parcels",
                    },
                }],
                [],
                new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }));

        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        _fixture = new WebAppFixture()
            .ReplaceService<ILayerPublishingService>(_publishing)
            .ReplaceService<IMetadataV2GraphProvider>(_graphProvider)
            .ReplaceService<IGeoprocessingJobService>(_jobService)
            .ReplaceService<ISecureConnectionResolver>(resolver)
            .ReplaceService<IOperatorApprovalEvaluator>(_approvalEvaluator)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
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
            $"publish-reader-{Guid.NewGuid():N}",
            ["admin:read"],
            DateTimeOffset.UtcNow.AddMinutes(15),
            "publish-authorization-test",
            CancellationToken.None)).Key;
        _adminWriteKey = (await keyStore.CreateAsync(
            $"publish-writer-{Guid.NewGuid():N}",
            ["admin:write"],
            DateTimeOffset.UtcNow.AddMinutes(15),
            "publish-authorization-test",
            CancellationToken.None)).Key;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    [Endpoint("POST /mcp tools/call honua_publish_result")]
    [Endpoint("POST /api/v1/operations/service.publish/submit")]
    public async Task PublishAuthority_RealTransportTwentyFourCaseMatrix_MatchesRest()
    {
        var cases = (
            from tool in Enum.GetValues<PublishTool>()
            from profile in Enum.GetValues<PrincipalProfile>()
            from approvalRequired in ApprovalDecisions
            select new MatrixCase(tool, profile, approvalRequired)).ToArray();

        cases.Should().HaveCount(24);

        foreach (var testCase in cases)
        {
            _approvalEvaluator.IsRequired = testCase.ApprovalRequired;
            var expected = testCase.Profile == PrincipalProfile.AdminWrite
                ? testCase.ApprovalRequired ? AdmissionOutcome.RequiresApproval : AdmissionOutcome.Allowed
                : AdmissionOutcome.Denied;

            using var restClient = CreateClient(testCase.Profile);
            var restPolicyRef = await AssertRestTwinAsync(restClient, expected, testCase);

            using var mcpClient = CreateClient(testCase.Profile);
            await AssertMcpAsync(mcpClient, expected, restPolicyRef, testCase);
        }
    }

    private async Task<string?> AssertRestTwinAsync(
        HttpClient client,
        AdmissionOutcome expected,
        MatrixCase testCase)
    {
        ClearSpies();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/operations/service.publish/submit",
            new
            {
                connectionId = ConnectionId,
                serviceName = "default",
                parameters = new Dictionary<string, string>
                {
                    ["schema"] = "public",
                    ["table"] = "parcels",
                    ["layerName"] = "Parcels",
                },
            });
        var body = await response.Content.ReadAsStringAsync();

        if (expected == AdmissionOutcome.Allowed)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{Describe(testCase, "REST")}: {body}");
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("status").GetString().Should().Be("Completed", body);
            data.GetProperty("metadataRevision").GetInt64().Should().Be(42, body);
            await AssertSingleActuationAsync();
            return null;
        }

        response.StatusCode.Should().Be(
            testCase.Profile == PrincipalProfile.Anonymous
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.Forbidden,
            Describe(testCase, "REST"));
        await AssertZeroActuationAsync();
        if (expected != AdmissionOutcome.RequiresApproval)
        {
            return null;
        }

        using var approvalDocument = JsonDocument.Parse(body);
        return approvalDocument.RootElement.GetProperty("policyRef").GetString();
    }

    private async Task AssertMcpAsync(
        HttpClient client,
        AdmissionOutcome expected,
        string? restPolicyRef,
        MatrixCase testCase)
    {
        ClearSpies();
        using var request = BuildMcpRequest(testCase.Tool);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{Describe(testCase, "MCP")}: {body}");

        using var document = JsonDocument.Parse(body);
        var result = document.RootElement.GetProperty("result");
        if (expected == AdmissionOutcome.Allowed)
        {
            result.GetProperty("isError").GetBoolean().Should().BeFalse(Describe(testCase, "MCP"));
            result.GetProperty("structuredContent").GetProperty("metadataRevision").GetInt64()
                .Should().Be(42);
            await AssertSingleActuationAsync();
            if (testCase.Tool == PublishTool.PublishResult)
            {
                await _jobService.Received(1).GetJobResultsAsync(
                    "publish-matrix-job",
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>());
            }
            return;
        }

        result.GetProperty("isError").GetBoolean().Should().BeTrue(Describe(testCase, "MCP"));
        var expectedCode = expected == AdmissionOutcome.RequiresApproval
            ? "failed_precondition"
            : testCase.Profile == PrincipalProfile.Anonymous ? "unauthenticated" : "permission_denied";
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be(expectedCode, Describe(testCase, "MCP"));
        if (expected == AdmissionOutcome.RequiresApproval)
        {
            restPolicyRef.Should().Be("operator.publish");
            result.GetProperty("structuredContent").GetProperty("policyRef").GetString()
                .Should().Be(restPolicyRef, "MCP must expose the policy selected by the REST-equivalent gate");
        }
        await AssertZeroActuationAsync();
        await _jobService.DidNotReceive().GetJobResultsAsync(
            Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    private void ClearSpies()
    {
        _publishing.ClearReceivedCalls();
        _graphProvider.ClearReceivedCalls();
        _jobService.ClearReceivedCalls();
    }

    private async Task AssertSingleActuationAsync()
    {
        await _publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        await _graphProvider.Received(1).GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    private async Task AssertZeroActuationAsync()
    {
        await _publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        await _graphProvider.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    private HttpClient CreateClient(PrincipalProfile profile) => profile switch
    {
        PrincipalProfile.Anonymous => _fixture.CreateClient(),
        PrincipalProfile.AuthenticatedNoGrant => CreateBearerClient(scope: null),
        PrincipalProfile.SourceJobOwnerReadOnly => CreateBearerClient(OperatorScopeCatalog.Read),
        PrincipalProfile.AdminRead => CreateApiKeyClient(_adminReadKey),
        PrincipalProfile.OAuthPublishScope => CreateBearerClient(OperatorScopeCatalog.Publish),
        PrincipalProfile.AdminWrite => CreateApiKeyClient(_adminWriteKey),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private HttpClient CreateApiKeyClient(string key) =>
        _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));

    private HttpClient CreateBearerClient(string? scope)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, $"publish-principal-{Guid.NewGuid():N}"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // Reach publish authorization with a tenant-bound authenticated principal.
            new("tid", "publish-authorization-tenant"),
        };
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
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        return _fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", encoded));
    }

    private static HttpRequestMessage BuildMcpRequest(PublishTool tool)
    {
        object arguments = tool switch
        {
            PublishTool.PublishService => new
            {
                connectionId = ConnectionId,
                schema = "public",
                table = "parcels",
                layerName = "Parcels",
            },
            PublishTool.PublishResult => new
            {
                sourceId = "publish-matrix-job",
                artifactId = "publish-matrix-artifact",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
        var name = tool == PublishTool.PublishService
            ? "honua_publish_service"
            : "honua_publish_result";
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = $"publish-{tool}",
            method = "tools/call",
            @params = new { name, arguments },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string Describe(MatrixCase testCase, string surface) =>
        $"surface={surface}, tool={testCase.Tool}, profile={testCase.Profile}, " +
        $"approvalRequired={testCase.ApprovalRequired}";

    private sealed class MutableApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public bool IsRequired { get; set; }

        public ApprovalRequirement Evaluate(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request) =>
            IsRequired
                ? ApprovalRequirement.Required("operator.publish", "publish-requires-approval")
                : ApprovalRequirement.NotRequired();
    }

    private enum PublishTool
    {
        PublishService,
        PublishResult,
    }

    private enum PrincipalProfile
    {
        Anonymous,
        AuthenticatedNoGrant,
        SourceJobOwnerReadOnly,
        AdminRead,
        OAuthPublishScope,
        AdminWrite,
    }

    private enum AdmissionOutcome
    {
        Denied,
        RequiresApproval,
        Allowed,
    }

    private sealed record MatrixCase(
        PublishTool Tool,
        PrincipalProfile Profile,
        bool ApprovalRequired);
}
