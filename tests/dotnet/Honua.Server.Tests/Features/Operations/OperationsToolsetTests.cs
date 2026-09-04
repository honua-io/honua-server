// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using IOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// In-memory unit coverage for the Honua Operations Toolset de-risking spike: the grounding
/// catalog lists the <c>service.publish</c> descriptor; the executor wraps the real
/// <see cref="ILayerPublishingService"/>; the dispatcher consults the policy decision point
/// and short-circuits the executor on a Deny decision (the guardrail seam).
/// </summary>
public sealed class OperationsToolsetTests
{
    private const string TestConnectionId = "11111111-1111-1111-1111-111111111111";

    [UnitTest]
    public void AddOperationsToolset_RegistersServicePublishApprovalMapperAndCanonicalActuator()
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationApprovalRequestMapper) &&
            descriptor.ImplementationType == typeof(ServicePublishApprovalRequestMapper));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationType == typeof(DeferredServicePublishExecutor));
        services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IOperationApprovalRequestMapper) &&
                descriptor.ImplementationInstance is StudioDraftApprovalRequestMapper)
            .Select(descriptor =>
                ((StudioDraftApprovalRequestMapper)descriptor.ImplementationInstance!).OperationId)
            .Should().BeEquivalentTo(
                StudioDraftOperations.Create,
                StudioDraftOperations.Update,
                StudioDraftOperations.Delete,
                StudioDraftOperations.Validate,
                StudioDraftOperations.PreviewPlan,
                StudioDraftOperations.SaveVersion,
                StudioDraftOperations.CreatePublicationRequest,
                StudioDraftOperations.ReopenVersion,
                StudioDraftOperations.Rollback);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationType == typeof(StudioDraftDeleteExecutor));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationType == typeof(StudioCreatePublicationRequestExecutor));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationType == typeof(StudioReopenVersionExecutor));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationType == typeof(StudioRollbackExecutor));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IOperationEnvelopeFactory) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor) &&
            descriptor.ImplementationType != null &&
            descriptor.ImplementationType.Name.Contains("ServicePublish", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task AddOperationsToolset_WithoutPublishGraph_ResolvesStudioRuntimeAndRefusesPublishActionably()
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        services.AddSingleton(Substitute.For<Honua.Core.Features.Studio.Abstractions.IStudioPackageLifecycleService>());
        services.AddSingleton(Substitute.For<IReadinessCheckService>());

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executors = scope.ServiceProvider.GetServices<IOperationExecutor>().ToArray();
        executors.Should().Contain(executor => executor.OperationId == StudioDraftOperations.Create);

        var publish = executors.Should().ContainSingle(executor =>
            executor.OperationId == ServicePublishOperation.OperationId).Subject;
        var validation = await publish.ValidateAsync(BuildRequest());
        validation.IsValid.Should().BeFalse();
        validation.Status.Should().Be("unavailable");
        validation.Messages.Should().ContainSingle(message =>
            message.Contains(nameof(IMetadataV2GraphProvider), StringComparison.Ordinal) &&
            message.Contains("not registered", StringComparison.Ordinal));
    }

    [UnitTest]
    public void AddOperationsToolset_ProductionWithoutProposalStore_DoesNotRegisterDurableRuntimeHostedServices()
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType != null &&
            (descriptor.ImplementationType == typeof(OperationRuntimeStartupValidator) ||
             descriptor.ImplementationType == typeof(PlannedProposalReconciler) ||
             descriptor.ImplementationType == typeof(QueuedOperationReconciler)));
    }

    [UnitTest]
    public void AddOperationsToolset_ProductionWithProposalStore_RegistersDurableRuntimeHostedServices()
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(Substitute.For<IOperationProposalStore>());

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(OperationRuntimeStartupValidator));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(PlannedProposalReconciler));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(QueuedOperationReconciler));
    }

    [UnitTest]
    public void AddOperationsToolset_WithoutProposalStore_UsesFailClosedReplayVerifierAndBoots()
    {
        // Regression: the real replay verifier requires IOperationProposalStore in its
        // constructor and the dispatcher requires a verifier, so no-store hosts failed
        // ValidateOnBuild at boot (trunk red, run 33241561197). Degraded hosts must
        // compose, and replay verification must refuse without the durable authority.
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IOperationApprovalReplayVerifier>();
        verifier.Should().BeOfType<UnavailableOperationApprovalReplayVerifier>();
        verifier.VerifyAsync("proposal", "instance", "hash").Result.Should().BeFalse(
            "replay can never verify without the durable proposal authority");
    }

    [UnitTest]
    public void AddOperationsToolset_WithProposalStore_UsesDurableReplayVerifier()
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(Substitute.For<IOperationProposalStore>());

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOperationApprovalReplayVerifier>()
            .Should().BeOfType<OperationApprovalReplayVerifier>();
    }

    [UnitTest]
    public void AddOperationsToolset_StoreRegisteredAfterComposition_UsesDurableReplayVerifier()
    {
        // The reviewer's exact scenario: fixtures add the proposal store AFTER
        // AddOperationsToolset (post-Program ConfigureServices). Resolution-time
        // selection must honor the final composition, not a registration snapshot.
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        services.AddSingleton(Substitute.For<IOperationProposalStore>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOperationApprovalReplayVerifier>()
            .Should().BeOfType<OperationApprovalReplayVerifier>();
    }

    [UnitTest]
    public void AddOperationsToolset_RegistersLaneAAndLaneBOnlyWithDurableProposalStore_AndIsIdempotent()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var degraded = new ServiceCollection();

        degraded.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        degraded.Should().NotContain(descriptor =>
            descriptor.ImplementationType == typeof(AdminConnectImportOperationDescriptorProvider));
        degraded.Should().NotContain(descriptor => descriptor.ServiceType ==
            typeof(OperationsServiceCollectionExtensions.AdminConnectImportRegistrationMarker));
        degraded.Should().NotContain(descriptor =>
            descriptor.ImplementationType == typeof(AdminApiOperationDescriptorProvider));
        degraded.Should().NotContain(descriptor => descriptor.ServiceType ==
            typeof(OperationsServiceCollectionExtensions.AdminApiOperationRegistrationMarker));

        var composed = new ServiceCollection();
        composed.AddSingleton(Substitute.For<IOperationProposalStore>());
        composed.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        composed.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        composed.Count(descriptor => descriptor.ImplementationType ==
            typeof(AdminConnectImportOperationDescriptorProvider)).Should().Be(1);
        composed.Count(descriptor => descriptor.ImplementationType ==
            typeof(AdminApiOperationDescriptorProvider)).Should().Be(1);
        composed.Count(descriptor => descriptor.ServiceType == typeof(IOperationExecutor) &&
            descriptor.ImplementationFactory != null).Should().Be(
                AdminConnectImportOperationCatalog.Definitions.Count +
                AdminApiOperationCatalog.Definitions.Count +
                (AdminOperateOperationCatalog.Definitions.Count * 2) + 4,
                "Lanes A and B are idempotent, Lane D composes on each call, and the four legacy adapters remain unique");
    }

    [UnitTest]
    public void AddOperationsToolset_WithControlPlaneExecutors_RegistersLegacyAdaptersWithoutThrowing()
    {
        // Regression: the legacy adapters are factory descriptors, and registering
        // them via TryAddEnumerable threw ArgumentException ("indistinguishable
        // from other services") the moment a control-plane executor was present —
        // i.e. in every full host, but in no unit-scoped test host, which is how
        // it reached trunk (run 33237378473).
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var controlPlaneExecutor =
            Substitute.For<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor>();
        services.AddScoped(_ => controlPlaneExecutor);

        var register = () => services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);

        register.Should().NotThrow();
        services.Count(descriptor =>
                descriptor.ServiceType == typeof(IOperationExecutor) &&
                descriptor.ImplementationFactory != null)
            .Should().Be(
                AdminOperateOperationCatalog.Definitions.Count + 4,
                "each admin operation and legacy operation class gets one factory-registered executor");

        // Idempotence across repeated composition, previously TryAddEnumerable's job.
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        services.Count(descriptor =>
                descriptor.ServiceType == typeof(IOperationExecutor) &&
                descriptor.ImplementationFactory != null)
            .Should().Be(
                (AdminOperateOperationCatalog.Definitions.Count * 2) + 4,
                "re-registration must not duplicate the legacy adapters even though admin executors are added again");
    }

    [UnitTest]
    public async Task Catalog_Lists_ServicePublish_Descriptor_With_ExecutionKind_ApprovalModel_And_Policy()
    {
        var catalog = new OperationCatalog(
            [new ServerOperationDescriptorProvider()],
            TimeProvider.System);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = snapshot.Operations.Should().ContainSingle(op => op.OperationId == "service.publish").Subject;
        descriptor.ExecutionKind.Should().Be(OperationExecutionKind.Synchronous);
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.ServiceScope);
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        descriptor.Policy.Determinism.Should().Be(OperationDeterminism.Deterministic);
        descriptor.Policy.SupportsDryRun.Should().BeTrue();

        // GetDescriptorAsync resolves the same descriptor by id.
        var resolved = await catalog.GetDescriptorAsync("service.publish", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be("honua.server.operations");
    }

    [UnitTest]
    public async Task Catalog_Lists_AdminStatus_Descriptor_With_ReadOnly_Convention_Metadata()
    {
        var catalog = new OperationCatalog(
            [new ServerOperationDescriptorProvider()],
            TimeProvider.System);

        var descriptor = (await catalog.GetSnapshotAsync(CancellationToken.None))
            .Operations.Should().ContainSingle(op => op.OperationId == "admin.server.status").Subject;

        descriptor.Category.Should().Be("admin");
        descriptor.ExecutionKind.Should().Be(OperationExecutionKind.Synchronous);
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.None);
        descriptor.InputSchema.Should().BeEmpty();
        descriptor.OutputSchema.Select(parameter => parameter.Name)
            .Should().BeEquivalentTo(["status", "version"]);
        descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.None);
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.ReadOnly);
        descriptor.Policy.Determinism.Should().Be(OperationDeterminism.RuntimeDynamic);
    }

    [UnitTest]
    public async Task LaneD_AdminOperations_RoundTrip_FromCatalog_ToPublishedTools_WhenEnabled()
    {
        var catalog = new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);
        var descriptors = snapshot.Operations
            .Where(operation => AdminOperateOperationCatalog.Definitions.Any(definition => definition.OperationId == operation.OperationId))
            .ToArray();
        var publishedNames = (await source.GetToolsAsync(CancellationToken.None))
            .Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        descriptors.Should().HaveCount(AdminOperateOperationCatalog.Definitions.Count);
        foreach (var descriptor in descriptors)
        {
            var projectedName = PublishedOperationTool.ProjectName(descriptor.OperationId);
            if (descriptor.ApprovalModel == OperationApprovalModel.OperatorGate)
                publishedNames.Should().NotContain(projectedName);
            else
                publishedNames.Should().Contain(projectedName);
        }
    }

    [UnitTest]
    public async Task LaneB_AdminOperations_RoundTrip_FromCatalog_ToPublishedTools_WhenEnabled()
    {
        var catalog = new OperationCatalog([new AdminApiOperationDescriptorProvider()], TimeProvider.System);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: AdminApiOperationCatalog.Definitions
                .Where(static definition => definition.Destructive)
                .Select(static definition => new AdminApiOperationApprovalRequestMapper(definition)));

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);
        var laneBDescriptors = snapshot.Operations
            .Where(operation => AdminApiOperationCatalog.Definitions.Any(definition => definition.OperationId == operation.OperationId))
            .ToArray();
        var publishedNames = (await source.GetToolsAsync(CancellationToken.None)).Select(static tool => tool.Name);

        laneBDescriptors.Should().HaveCount(AdminApiOperationCatalog.Definitions.Count);
        publishedNames.Should().BeEquivalentTo(
            laneBDescriptors.Select(static descriptor => PublishedOperationTool.ProjectName(descriptor.OperationId)));
        laneBDescriptors.Where(static descriptor => descriptor.Policy.SideEffectClass != OperationSideEffectClass.ReadOnly)
            .Should().OnlyContain(static descriptor => descriptor.ApprovalModel == OperationApprovalModel.OperatorGate);
    }

    [UnitTest]
    public async Task LaneC_AccessOperations_RoundTrip_FromOpenApiCatalog_ToEligiblePublishedTools()
    {
        var catalog = new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System);
        var mappers = AdminAccessOperationCatalog.Definitions
            .Where(static definition => definition.SideEffect != OperationSideEffectClass.ReadOnly)
            .Select(static definition => new AdminOperateOperationApprovalRequestMapper(definition))
            .ToArray();
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: mappers);

        var descriptors = (await catalog.GetSnapshotAsync(CancellationToken.None)).Operations;
        var tools = await source.GetToolsAsync(CancellationToken.None);
        var expected = descriptors
            .Where(descriptor => !AdminMcpOperationExclusions.ContainsOperation(descriptor.OperationId))
            .Select(descriptor => PublishedOperationTool.ProjectName(descriptor.OperationId));

        descriptors.Should().HaveCount(AdminAccessOperationCatalog.Definitions.Count);
        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(expected);
        tools.Select(static tool => tool.Name).Should().Contain(
            "honua_admin_api_key_list", "honua_admin_api_key_effective_permissions");
        tools.Select(static tool => tool.Name).Should().NotContain(
            "honua_admin_api_key_create", "honua_admin_api_key_rotate", "honua_admin_oauth_client_register");
    }

    [UnitTest]
    public void LaneC_DescriptorsAndExclusions_DiffAgainstCurrentAdminOpenApi()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var openApiIds = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(static path => path.Value.EnumerateObject())
            .Where(static method => method.Value.ValueKind == JsonValueKind.Object &&
                method.Value.TryGetProperty("operationId", out _))
            .Select(static method => method.Value.GetProperty("operationId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var definition in AdminAccessOperationCatalog.Definitions)
        {
            openApiIds.Should().Contain(definition.OpenApiOperationId);
            AdminAccessOperationCatalog.Descriptors.Should().ContainSingle(
                descriptor => descriptor.OperationId == definition.OperationId);
        }

        AdminMcpOperationExclusions.All.Select(static exclusion => exclusion.OpenApiOperationId)
            .Should().OnlyContain(openApiId => openApiIds.Contains(openApiId));
        AdminMcpOperationExclusions.All.Should().ContainSingle(entry =>
            entry.OperationId == "admin.api-key.create" &&
            entry.ReasonCode == AdminMcpOperationExclusions.OneTimeSecretReasonCode);
        AdminMcpOperationExclusions.All.Should().ContainSingle(entry =>
            entry.OperationId == "admin.api-key.rotate" &&
            entry.ReasonCode == AdminMcpOperationExclusions.OneTimeSecretReasonCode);
        AdminMcpOperationExclusions.All.Should().ContainSingle(entry =>
            entry.OperationId == "admin.oauth-client.register" &&
            entry.ReasonCode == AdminMcpOperationExclusions.OneTimeSecretReasonCode);
        AdminMcpOperationExclusions.All.Should().ContainSingle(entry =>
            entry.OperationId == "admin.oidc-provider.create" &&
            entry.ReasonCode == AdminMcpOperationExclusions.SecretInputReasonCode);
        AdminMcpOperationExclusions.All.Should().ContainSingle(entry =>
            entry.OperationId == "admin.oidc-provider.update" &&
            entry.ReasonCode == AdminMcpOperationExclusions.SecretInputReasonCode);
        AdminMcpOperationExclusions.Digest.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [UnitTest]
    public void LaneC_EachDescriptorHasOneExecutor_AndEachMutationHasOneReplayMapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddAdminAccessOperations().AddAdminAccessOperations();
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var eligible = AdminAccessOperationCatalog.Definitions
            .Where(static definition => !AdminMcpOperationExclusions.RequiresSecretAwareRuntime(definition.OperationId))
            .ToArray();
        scope.ServiceProvider.GetServices<IOperationExecutor>()
            .Select(static executor => executor.OperationId)
            .Should().BeEquivalentTo(eligible.Select(static definition => definition.OperationId));
        services.Where(static descriptor => descriptor.ServiceType == typeof(IOperationApprovalRequestMapper) &&
                descriptor.ImplementationInstance is AdminOperateOperationApprovalRequestMapper)
            .Select(static descriptor => ((IOperationApprovalRequestMapper)descriptor.ImplementationInstance!).OperationId)
            .Should().BeEquivalentTo(eligible
                .Where(static definition => definition.SideEffect != OperationSideEffectClass.ReadOnly)
                .Select(static definition => definition.OperationId));
    }

    [UnitTest]
    public Task LaneC_ApiKeyCreate_IsRefusedBeforeAcceptance() =>
        AssertSecretOperationUnavailableAsync("admin.api-key.create");

    [UnitTest]
    public Task LaneC_ApiKeyRotate_IsRefusedBeforeAcceptance() =>
        AssertSecretOperationUnavailableAsync("admin.api-key.rotate");

    [UnitTest]
    public Task LaneC_OAuthClientRegister_IsRefusedBeforeAcceptance() =>
        AssertSecretOperationUnavailableAsync("admin.oauth-client.register");

    [UnitTest]
    public Task LaneC_OidcProviderCreate_IsRefusedBeforeAcceptance() =>
        AssertSecretOperationUnavailableAsync("admin.oidc-provider.create");

    [UnitTest]
    public Task LaneC_OidcProviderUpdate_IsRefusedBeforeAcceptance() =>
        AssertSecretOperationUnavailableAsync("admin.oidc-provider.update");

    [UnitTest]
    public async Task LaneC_RemainingAccessOperations_RegisterAndExecute()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddAdminAccessOperations();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var eligible = AdminAccessOperationCatalog.Definitions
            .Where(static definition => !AdminMcpOperationExclusions.RequiresSecretAwareRuntime(definition.OperationId))
            .ToArray();
        scope.ServiceProvider.GetServices<IOperationExecutor>()
            .Select(static executor => executor.OperationId)
            .Should().BeEquivalentTo(eligible.Select(static definition => definition.OperationId));

        var handler = new CapturingOperationHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/admin/api-keys");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"keys\":[]}")
            });
        });
        using var client = new HttpClient(handler);
        var executor = BuildAccessAdminExecutor("admin.api-key.list", client);

        var handle = await executor.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?>()
            },
            new OperationPolicyContext());

        handle.Status.Should().Be(OperationHandleStatus.Completed);
    }

    [UnitTest]
    public async Task LaneC_Validation_RejectsMissingRequiredBodyParametersBeforeApproval()
    {
        var definition = AdminAccessOperationCatalog.Definitions.Single(
            item => item.OperationId == "admin.api-key.create");
        var descriptor = AdminAccessOperationCatalog.Descriptors.Single(
            item => item.OperationId == definition.OperationId);
        var executor = new AdminOperateOperationExecutor(
            definition,
            descriptor,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IHttpContextAccessor>(),
            null,
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var invalid = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = definition.OperationId,
            Parameters = new Dictionary<string, string?>()
        });
        var valid = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = definition.OperationId,
            Parameters = new Dictionary<string, string?> { ["name"] = "automation" }
        });

        invalid.IsValid.Should().BeFalse();
        invalid.Messages.Should().Contain("Required parameter 'name' is missing.");
        valid.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void LaneD_DescriptorSchemas_DiffCleanlyAgainstAdminApiComponents()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Honua.TestKit.RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));

        foreach (var definition in AdminOperateOperationCatalog.Definitions)
        {
            var operation = AdminOperateOperationCatalog.FindOperation(document.RootElement, definition.OpenApiOperationId);
            operation.GetProperty("operationId").GetString().Should().Be(definition.OpenApiOperationId);
            var descriptor = AdminOperateOperationCatalog.Descriptors.Should()
                .ContainSingle(item => item.OperationId == definition.OperationId).Subject;
            descriptor.InputSchema.Should().NotBeNull();
            descriptor.OutputSchema.Should().NotBeNull();
        }
    }

    [UnitTest]
    public void LaneB_DescriptorSchemas_AreProjectedFromAdminApiComponents()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Honua.TestKit.RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));

        foreach (var definition in AdminApiOperationCatalog.Definitions)
        {
            var operation = AdminApiOperationCatalog.FindOperation(document.RootElement, definition.OpenApiOperationId);
            operation.GetProperty("operationId").GetString().Should().Be(definition.OpenApiOperationId);
            AdminApiOperationCatalog.Descriptors.Should().ContainSingle(
                descriptor => descriptor.OperationId == definition.OperationId,
                "every lane-B descriptor must be built from an operation in admin-api.json");
        }
    }

    [UnitTest]
    public void LaneB_ApprovalPayload_PersistsRequesterTenantAndSchema()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(
            item => item.OperationId == "admin.layer.set-enabled");
        var mapper = new AdminApiOperationApprovalRequestMapper(definition);
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(
            item => item.OperationId == definition.OperationId);
        var request = new OperationRequest
        {
            OperationId = definition.OperationId,
            ConnectionId = "connection-1",
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "7",
                ["enabled"] = "true"
            }
        };

        var mapped = mapper.Map(descriptor, request, new OperationPolicyContext
        {
            TenantId = "requester-tenant",
            SchemaName = "tenant_schema"
        }, new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        var replay = mapper.MapReplay(mapped);

        replay.TenantId.Should().Be("requester-tenant");
        replay.SchemaName.Should().Be("tenant_schema");
        replay.Request.ConnectionId.Should().Be("connection-1");
        replay.Request.Parameters.Should().Contain("layerId", "7").And.Contain("enabled", "true");
    }

    [UnitTest]
    public async Task LaneB_ApprovedReplay_MintsScopedCredential_AndUsesRequesterTenant()
    {
        var credentialStore = new InMemoryAdminApiKeyStore(TimeProvider.System);
        var approver = await credentialStore.CreateAsync(
            "approve-only", ["admin:approve"], null, "approver", CancellationToken.None);
        AdminApiKeyValidationResult? executionAuthority = null;
        var handler = new CapturingOperationHandler(async request =>
        {
            var executionKey = request.Headers.GetValues("X-API-Key").Single();
            executionKey.Should().NotBe(approver.Key);
            request.Headers.GetValues("X-Honua-Tenant").Should().Equal("requester-tenant");
            request.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().Equal("opinst-api-exact");
            request.Headers.GetValues("X-Correlation-ID").Should().Equal("corr-api-exact");
            request.Headers.GetValues("X-Honua-Audit-Id").Should().Equal("audit-api-exact");
            request.Headers.GetValues("X-Honua-Proposal-Id").Should().Equal("proposal-1");
            executionAuthority = await credentialStore.ValidateAsync(executionKey, CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        });
        var factory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(handler);
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(httpClient);
        var current = new DefaultHttpContext();
        current.Request.Scheme = "https";
        current.Request.Host = new HostString("localhost");
        current.Connection.LocalPort = 443;
        current.Request.Headers["X-API-Key"] = approver.Key;
        current.Request.Headers["X-Honua-Tenant"] = "approver-tenant";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(current);
        var definition = AdminApiOperationCatalog.Definitions.Single(
            item => item.OperationId == "admin.layer.set-enabled");
        var executor = new AdminApiOperationExecutor(
            definition, factory, accessor, credentialStore, TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = definition.OperationId,
            ConnectionId = "connection-1",
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "7",
                ["enabled"] = "true"
            }
        }, new OperationPolicyContext
        {
            ApprovedProposalId = "proposal-1",
            OperationInstanceId = "opinst-api-exact",
            CorrelationId = "corr-api-exact",
            AuditId = "audit-api-exact",
            TenantId = "requester-tenant",
            PrincipalId = "requester"
        }, CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        executionAuthority.Should().NotBeNull();
        // Approved replays must carry an exact method/path grant; the old broad admin:write
        // assertion described the authorization bug this test is intended to prevent.
        executionAuthority!.Record.Permissions.Should().Equal(
            AdminApiKeyPermission.CreateApprovedOperationGrant(
                definition.Method.Method,
                "/api/v1/admin/connections/connection-1/layers/7/enabled"));
        (await credentialStore.GetAsync(executionAuthority.Record.Id, CancellationToken.None))!
            .RevokedAt.Should().NotBeNull("operation credentials are single-use");
    }

    [UnitTest]
    public void LaneD_DestructiveOperations_AreApprovalGated_AndRollbackIsTruthful()
    {
        var destructive = AdminOperateOperationCatalog.Descriptors
            .Where(descriptor => descriptor.Policy.SideEffectClass != OperationSideEffectClass.ReadOnly).ToArray();
        destructive.Where(descriptor => descriptor.OperationId != "admin.metadata.prevalidate")
            .Should().OnlyContain(descriptor => descriptor.ApprovalModel == OperationApprovalModel.OperatorGate);

        var rollback = destructive.Should().ContainSingle(
            descriptor => descriptor.OperationId == "admin.metadata.coordinated-releases.rollback").Subject;
        rollback.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.DestroysState);
        rollback.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.DeploymentScope);
        rollback.Policy.SupportsDryRun.Should().BeFalse(
            "#3301 has not landed and the coordinated release endpoint exposes no rollback dry run");
    }

    [UnitTest]
    public void LaneD_EachDescriptorHasARegisteredExecutor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        using var provider = services.BuildServiceProvider();
        var executorIds = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IOperationExecutor) && descriptor.ImplementationFactory is not null)
            .Select(descriptor => (IOperationExecutor)descriptor.ImplementationFactory!(provider))
            .Select(static executor => executor.OperationId).ToHashSet(StringComparer.Ordinal);

        executorIds.Should().Contain(AdminOperateOperationCatalog.Definitions.Select(static definition => definition.OperationId));
    }

    [UnitTest]
    public void LaneD_ApprovalGatedOperations_HaveExactlyOneReplayMapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        using var provider = services.BuildServiceProvider();
        var mapperCounts = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IOperationApprovalRequestMapper) &&
                descriptor.ImplementationInstance is AdminOperateOperationApprovalRequestMapper)
            .Select(static descriptor => (IOperationApprovalRequestMapper)descriptor.ImplementationInstance!)
            .GroupBy(static mapper => mapper.OperationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        var approvalGated = AdminOperateOperationCatalog.Descriptors
            .Where(static descriptor => descriptor.ApprovalModel != OperationApprovalModel.None)
            .Select(static descriptor => descriptor.OperationId)
            .ToArray();
        mapperCounts.Keys.Should().BeEquivalentTo(approvalGated);
        mapperCounts.Should().OnlyContain(static pair => pair.Value == 1);
    }

    [UnitTest]
    public void LaneD_ApprovalMappers_PreserveOperationGuardrailClass()
    {
        var metadataReleaseOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "admin.metadata.release-packages.create",
            "admin.metadata.releases.activate",
            "admin.metadata.coordinated-releases.rollback"
        };

        foreach (var definition in AdminOperateOperationCatalog.Definitions
                     .Where(static definition => definition.ApprovalModel != OperationApprovalModel.None &&
                         definition.SideEffect != OperationSideEffectClass.ReadOnly))
        {
            var descriptor = AdminOperateOperationCatalog.Descriptors.Single(
                descriptor => descriptor.OperationId == definition.OperationId);
            var request = new OperationRequest { OperationId = definition.OperationId };
            var mapped = new AdminOperateOperationApprovalRequestMapper(definition).Map(
                descriptor,
                request,
                new OperationPolicyContext(),
                new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

            mapped.Kind.Should().Be(
                metadataReleaseOperations.Contains(definition.OperationId)
                    ? OperationClass.MetadataRelease
                    : OperationClass.AdminConfigChange);
        }
    }

    [UnitTest]
    public void LaneD_PublishedSchemas_PreserveNestedRequiredMembers_AndAdvertiseDryRun()
    {
        var descriptor = AdminOperateOperationCatalog.Descriptors.Should().ContainSingle(
            item => item.OperationId == "admin.metadata.prevalidate").Subject;
        var tool = new PublishedOperationTool(descriptor, "test", NullLogger.Instance);

        var schema = tool.Describe().InputSchema;
        schema.GetProperty("properties").GetProperty("dryRun").GetProperty("type").GetString()
            .Should().Be("boolean");
        schema.GetProperty("properties").GetProperty("dataScripts").GetProperty("items")
            .GetProperty("required").EnumerateArray().Select(static item => item.GetString())
            .Should().Contain("scriptId");
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata,
            "the loopback POST requires an admin write credential, so semantic authorization must refuse admin:read before execution");
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.None,
            "prevalidation does not require operator approval; its write classification mirrors the transport credential only");
    }

    [UnitTest]
    public async Task LaneD_Executor_WritesAotSafeBody_WithoutRouteOrAbsentOptionalParameters()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"ok\":true}");
        using var client = new HttpClient(handler);
        var executor = BuildAdminExecutor("admin.metadata.coordinated-releases.rollback", client);
        var request = new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["operationId"] = "operation-1",
                ["reason"] = null,
                ["force"] = "true"
            }
        };

        var handle = await executor.SubmitAsync(request, new OperationPolicyContext
        {
            OperationInstanceId = "opinst-exact",
            CorrelationId = "corr-exact",
            AuditId = "audit-exact",
            ProposalId = "proposal-exact",
        }, CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handler.RequestUri!.AbsolutePath.Should().EndWith("/operations/operation-1/rollback");
        handler.Headers!.GetValues("X-API-Key").Should().Equal("secret");
        handler.Headers.GetValues("X-Honua-Tenant").Should().Equal("tenant-a");
        handler.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().Equal("opinst-exact");
        handler.Headers.GetValues("X-Correlation-ID").Should().Equal("corr-exact");
        handler.Headers.GetValues("X-Honua-Audit-Id").Should().Equal("audit-exact");
        handler.Headers.GetValues("X-Honua-Proposal-Id").Should().Equal("proposal-exact");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo("force");
        body.RootElement.GetProperty("force").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    public async Task LaneD_ApprovedReplay_UsesExactOperationCredential_ThenRevokesIt()
    {
        var credentialStore = new InMemoryAdminApiKeyStore(TimeProvider.System);
        AdminApiKeyValidationResult? executionAuthority = null;
        Uri? replayUri = null;
        string? replayHost = null;
        var handler = new CapturingOperationHandler(async request =>
        {
            replayUri = request.RequestUri;
            replayHost = request.Headers.Host;
            var executionKey = request.Headers.GetValues("X-API-Key").Single();
            executionAuthority = await credentialStore.ValidateAsync(executionKey, CancellationToken.None);
            request.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().Equal("opinst-replay");
            request.Headers.GetValues("X-Correlation-ID").Should().Equal("corr-replay");
            request.Headers.GetValues("X-Honua-Audit-Id").Should().Equal("audit-replay");
            request.Headers.GetValues("X-Honua-Proposal-Id").Should().Equal("proposal-1");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        });
        using var client = new HttpClient(handler);
        var executor = BuildAdminExecutor("admin.cache.invalidate", client, credentialStore);

        var handle = await executor.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { ["scope"] = "catalog" }
            },
            new OperationPolicyContext
            {
                ApprovedProposalId = "proposal-1",
                OperationInstanceId = "opinst-replay",
                CorrelationId = "corr-replay",
                AuditId = "audit-replay",
                PrincipalId = "requester",
                TenantId = "requester-tenant",
            },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        executionAuthority.Should().NotBeNull();
        replayUri.Should().Be("http://127.0.0.1:8080/api/v1/admin/cache/invalidate");
        replayHost.Should().Be("public.example.test");
        executionAuthority!.Record.Permissions.Should().Equal(
            "admin:operation:POST:/api/v1/admin/cache/invalidate");
        (await credentialStore.GetAsync(executionAuthority.Record.Id, CancellationToken.None))!
            .RevokedAt.Should().NotBeNull("approved operation credentials are single-use");
    }

    [UnitTest]
    public async Task LaneD_ApprovedReplay_SurfacesFailedCredentialRevocation()
    {
        using var client = new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{\"ok\":true}"));
        var credentialStore = Substitute.For<IAdminApiKeyStore>();
        var issued = await new InMemoryAdminApiKeyStore(TimeProvider.System).CreateAsync(
            "approved-operation:proposal-1",
            ["admin:operation:POST:/api/v1/admin/cache/invalidate"],
            DateTimeOffset.UtcNow.AddMinutes(5),
            "requester",
            CancellationToken.None);
        credentialStore.CreateAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(issued);
        credentialStore.RevokeAsync(issued.Record.Id, CancellationToken.None)
            .Returns((AdminApiKeyRecord?)null);
        var executor = BuildAdminExecutor("admin.cache.invalidate", client, credentialStore);

        var act = () => executor.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { ["scope"] = "catalog" }
            },
            new OperationPolicyContext
            {
                ApprovedProposalId = "proposal-1",
                PrincipalId = "requester",
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Failed to revoke approved-operation credential*");
    }

    [UnitTest]
    public async Task LaneD_Executor_MapsExpectedAdminFailureToStructuredHandle()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, "{\"detail\":\"invalid scope\"}");
        using var client = new HttpClient(handler);
        var executor = BuildAdminExecutor("admin.cache.invalidate", client);
        var request = new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["scope"] = "invalid" }
        };

        var handle = await executor.SubmitAsync(request, new OperationPolicyContext(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Reason.Should().Contain("HTTP 400");
        handle.Result!.Details.Should().Contain("statusCode", "400")
            .And.Contain("response", "{\"detail\":\"invalid scope\"}");
    }

    [UnitTest]
    public async Task LaneA_AdminOperations_RoundTrip_FromCatalog_ToPublishedTools_WhenEnabled()
    {
        var catalog = new OperationCatalog([new AdminConnectImportOperationDescriptorProvider()], TimeProvider.System);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: AdminConnectImportOperationCatalog.Definitions
                .Where(static definition => definition.SideEffect != OperationSideEffectClass.ReadOnly)
                .Select(static definition => new AdminConnectImportApprovalRequestMapper(definition)));

        var descriptors = (await catalog.GetSnapshotAsync(CancellationToken.None)).Operations;
        var tools = await source.GetToolsAsync(CancellationToken.None);

        descriptors.Should().HaveCount(AdminConnectImportOperationCatalog.Definitions.Count);
        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(
            descriptors.Select(static descriptor => PublishedOperationTool.ProjectName(descriptor.OperationId)));
        descriptors.Where(static descriptor => descriptor.Policy.SideEffectClass != OperationSideEffectClass.ReadOnly)
            .Should().OnlyContain(static descriptor => descriptor.ApprovalModel == OperationApprovalModel.OperatorGate);
    }

    [UnitTest]
    public void LaneA_DescriptorSchemas_AreProjectedFromAdminApiContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));

        foreach (var definition in AdminConnectImportOperationCatalog.Definitions)
        {
            var operation = AdminConnectImportOperationCatalog.FindOperation(document.RootElement, definition.OpenApiOperationId);
            operation.GetProperty("operationId").GetString().Should().Be(definition.OpenApiOperationId);
            AdminConnectImportOperationCatalog.Descriptors.Should().ContainSingle(
                descriptor => descriptor.OperationId == definition.OperationId);
        }

        var create = AdminConnectImportOperationCatalog.Descriptors.Single(
            descriptor => descriptor.OperationId == "admin.connections.create");
        create.InputSchema.Select(static parameter => parameter.Name).Should().Contain("secretReference");
        create.InputSchema.Single(static parameter => parameter.Name == "secretReference").Schema.Type
            .Should().Be(Honua.Core.Features.WorkflowPackages.Domain.WorkflowSchemaValueType.Text);

        var upload = AdminConnectImportOperationCatalog.Descriptors.Single(
            descriptor => descriptor.OperationId == "admin.import.upload");
        upload.InputSchema.Should().ContainSingle(parameter => parameter.Name == "fileName" && parameter.Required);
    }

    [UnitTest]
    public void LaneA_ApprovalPayload_PreservesTypedIdentity_AndRejectsInlinePassword()
    {
        var definition = AdminConnectImportOperationCatalog.Definitions.Single(
            item => item.OperationId == "admin.connections.create");
        var mapper = new AdminConnectImportApprovalRequestMapper(definition);
        var descriptor = AdminConnectImportOperationCatalog.Descriptors.Single(
            item => item.OperationId == definition.OperationId);
        var request = new OperationRequest
        {
            OperationId = definition.OperationId,
            Parameters = new Dictionary<string, string?> { ["password"] = "plaintext" }
        };

        var decision = new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval };
        var map = () => mapper.Map(descriptor, request, new OperationPolicyContext(), decision);

        map.Should().Throw<InvalidOperationException>().WithMessage("*secretReference*");

        var safe = mapper.Map(descriptor, request with
        {
            Parameters = new Dictionary<string, string?> { ["secretReference"] = "vault://connection" }
        }, new OperationPolicyContext(), decision);
        safe.OperationId.Should().Be(definition.OperationId);
    }

    [UnitTest]
    public async Task LaneA_Transport_PreservesText_DecodesFiles_AndProjectsQueuedJob()
    {
        using var json = AdminConnectImportOperationExecutor.BuildJson(
            new OperationRequest
            {
                OperationId = "admin.connections.create",
                Parameters = new Dictionary<string, string?> { ["name"] = "true", ["sslRequired"] = "true" }
            }, [], "admin.connections.create");
        using var jsonDocument = JsonDocument.Parse(await json.ReadAsStringAsync());
        jsonDocument.RootElement.GetProperty("name").GetString().Should().Be("true");
        jsonDocument.RootElement.GetProperty("sslRequired").ValueKind.Should().Be(JsonValueKind.True);

        using var multipart = AdminConnectImportOperationExecutor.BuildMultipart(
            new OperationRequest
            {
                OperationId = "admin.import.upload",
                Parameters = new Dictionary<string, string?>
                {
                    ["file"] = Convert.ToBase64String([0x01, 0x02, 0x03]),
                    ["fileName"] = "roads.geojson"
                }
            }, []);
        var file = multipart.Single(part => part.Headers.ContentDisposition?.Name == "file");
        (await file.ReadAsByteArrayAsync()).Should().Equal(0x01, 0x02, 0x03);
        file.Headers.ContentDisposition!.FileName.Should().Be("roads.geojson");

        var resources = AdminConnectImportOperationExecutor.ReadQueuedResources(
            "{\"jobId\":\"job-1\",\"statusUrl\":\"/jobs/job-1\",\"cancelUrl\":\"/jobs/job-1/cancel\"}");
        resources.Should().Contain(new KeyValuePair<string, string>("jobId", "job-1"));
        resources.Should().ContainKey("statusUrl").And.ContainKey("cancelUrl");
    }

    [UnitTest]
    public async Task LaneA_ApprovedReplay_DoesNotExecuteWithApproveOnlyKey_AndUsesRequesterTenant()
    {
        var credentialStore = new InMemoryAdminApiKeyStore(TimeProvider.System);
        var approver = await credentialStore.CreateAsync(
            "approve-only", ["admin:approve"], null, "approver", CancellationToken.None);
        AdminApiKeyValidationResult? executionAuthority = null;
        var handler = new CapturingOperationHandler(async request =>
        {
            var executionKey = request.Headers.GetValues("X-API-Key").Single();
            executionKey.Should().NotBe(approver.Key,
                "the approve-only transport credential must never become execution authority");
            request.Headers.GetValues("X-Honua-Tenant").Should().Equal("requester-tenant");
            request.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().Equal("opinst-connect-exact");
            request.Headers.GetValues("X-Correlation-ID").Should().Equal("corr-connect-exact");
            request.Headers.GetValues("X-Honua-Audit-Id").Should().Equal("audit-connect-exact");
            request.Headers.GetValues("X-Honua-Proposal-Id").Should().Equal("proposal-1");
            executionAuthority = await credentialStore.ValidateAsync(executionKey, CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        });
        var factory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(handler);
        factory.CreateClient(AdminConnectImportOperationExecutor.HttpClientName)
            .Returns(httpClient);
        var current = new DefaultHttpContext();
        current.Request.Scheme = "https";
        current.Request.Host = new HostString("localhost");
        current.Request.Headers["X-API-Key"] = approver.Key;
        current.Request.Headers["X-Honua-Tenant"] = "approver-tenant";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(current);
        var definition = AdminConnectImportOperationCatalog.Definitions.Single(
            item => item.OperationId == "admin.connections.create");
        var executor = new AdminConnectImportOperationExecutor(
            definition, factory, accessor, credentialStore, TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var handle = await executor.SubmitAsync(
            new OperationRequest
            {
                OperationId = definition.OperationId,
                Parameters = new Dictionary<string, string?>
                {
                    ["name"] = "roads",
                    ["provider"] = "postgis",
                    ["connectionString"] = "Host=database",
                }
            },
            new OperationPolicyContext
            {
                ApprovedProposalId = "proposal-1",
                OperationInstanceId = "opinst-connect-exact",
                CorrelationId = "corr-connect-exact",
                AuditId = "audit-connect-exact",
                TenantId = "requester-tenant",
                PrincipalId = "requester",
            },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        executionAuthority.Should().NotBeNull();
        executionAuthority!.Record.Permissions.Should().Equal(
            "admin:operation:POST:/api/v1/admin/connections");
        (await credentialStore.GetAsync(executionAuthority.Record.Id, CancellationToken.None))!
            .RevokedAt.Should().NotBeNull("operation credentials are single-use");
    }

    [UnitTest]
    public async Task AdminStatus_Uses_Canonical_Release_Version()
    {
        var readiness = Substitute.For<IReadinessCheckService>();
        readiness.CheckReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(ReadinessResult.Ready());
        var executor = new AdminServerStatusExecutor(readiness, TimeProvider.System);
        var request = new OperationRequest { OperationId = AdminServerStatusExecutor.OperationName };

        var handle = await executor.SubmitAsync(
            request,
            new OperationPolicyContext(),
            CancellationToken.None);

        handle.Result.Should().NotBeNull();
        handle.Result!.Details["version"].Should().Be(
            HonuaDeploymentIdentity.GetReleaseVersion(typeof(AdminServerStatusExecutor).Assembly));
    }

    [UnitTest]
    public async Task ValidateAsync_Delegates_To_ValidateTableForPublishAsync()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default"
            });
        var executor = BuildExecutor(publishing);

        var validation = await executor.ValidateAsync(BuildRequest(), CancellationToken.None);

        validation.IsValid.Should().BeTrue();
        validation.Status.Should().Be("valid");
        await publishing.Received(1).ValidateTableForPublishAsync(
            Arg.Any<string>(),
            Arg.Is<TablePublishValidationRequest>(r => r.Schema == "public" && r.Table == "parcels"),
            Arg.Any<CancellationToken>());
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_With_AllowAll_Calls_PublishLayer_And_Handle_Carries_MetadataRevision()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 7,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default"
            });

        const long expectedRevision = 42;
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = expectedRevision }, "\"etag\"", DateTimeOffset.UtcNow);
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider
            .GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var notifications = Substitute.For<IMcpNotificationPublisher>();
        var executor = BuildExecutor(publishing, graphProvider, notifications);
        var dispatcher = BuildDispatcher(executor, new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.MetadataRevision.Should().Be(expectedRevision);
        handle.Result.Should().NotBeNull();
        handle.Result!.Details["layerId"].Should().Be("7");
        handle.Result.Details["metadataRevision"].Should().Be("42");

        // Policy was Allow → publish was actually invoked with the mapped request.
        await publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(),
            Arg.Is<LayerPublishRequest>(r => r.Schema == "public" && r.Table == "parcels" && r.LayerName == "Parcels"),
            Arg.Any<CancellationToken>());
        notifications.Received(1).BroadcastResourcesListChanged();
        notifications.Received(1).BroadcastToolsListChanged();
    }

    [UnitTest]
    public async Task SubmitAsync_RequestCanceledAfterActuation_PersistsTerminalEnvelope()
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new VolatileOperationInstanceStore();
        var audit = new CancellationCheckingAuditLog();
        var executor = new CancelingAfterActuationExecutor(requestCancellation);
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [executor],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            instanceStore: store,
            auditLog: audit);

        var handle = await dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext(),
            requestCancellation.Token);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        (await store.GetAsync(handle.OperationInstanceId)).Should().BeEquivalentTo(handle);
        audit.CanceledWriteCount.Should().Be(0,
            "terminal evidence must use a bounded token independent of the disconnected request");
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedReplayWithNarrowSealedCeiling_RefusesWiderOperation()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var dispatcher = BuildDispatcher(
            BuildExecutor(publishing, Substitute.For<IMetadataV2GraphProvider>()),
            new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext
            {
                ApprovedProposalId = "proposal-1",
                ScopeGoverned = true,
                RecognizedScopes = [OperatorScopeCatalog.Read],
            },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Reason.Should().Be("Approved replay operation exceeds the sealed OAuth scope authority.");
        await publishing.DidNotReceiveWithAnyArgs().PublishLayerAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task SubmitAsync_ActuatorPropagatesCancellation_PersistsIndeterminateEnvelope()
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new VolatileOperationInstanceStore();
        var audit = new CancellationCheckingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [new CancelingDuringActuationExecutor(requestCancellation)],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            instanceStore: store,
            auditLog: audit);

        var handle = await dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext(),
            requestCancellation.Token);

        handle.Status.Should().Be(OperationHandleStatus.Indeterminate);
        handle.Reason.Should().Contain("side effects may have committed");
        (await store.GetAsync(handle.OperationInstanceId)).Should().BeEquivalentTo(handle);
        audit.CanceledWriteCount.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_CanceledDuringValidation_PersistsCancelledWithoutActuation()
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new VolatileOperationInstanceStore();
        var audit = new CancellationCheckingAuditLog();
        var executor = new CancelingDuringValidationExecutor(requestCancellation);
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [executor],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            instanceStore: store,
            auditLog: audit);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext(),
            requestCancellation.Token));

        exception.Should().NotBeNull();
        executor.SubmitCount.Should().Be(0);
        var cancelled = audit.Events.Should().ContainSingle(entry => entry.Action == "operation.cancelled").Subject;
        var envelope = await store.GetAsync(cancelled.ResourceId!);
        envelope.Should().NotBeNull();
        envelope!.Status.Should().Be(OperationHandleStatus.Cancelled);
        envelope.Reason.Should().Contain("no side effect occurred");
    }

    [UnitTest]
    public async Task SubmitAsync_QueuedActuation_WritesSubmittedSuccessAudit()
    {
        var audit = new CancellationCheckingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [new QueuedExecutor()],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            auditLog: audit);

        var handle = await dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext(),
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Queued);
        audit.Events.Should().ContainSingle(entry =>
            entry.Action == "operation.submitted" && entry.Outcome == AuditOutcome.Success);
        audit.Events.Should().NotContain(entry => entry.Action == "operation.completed");
    }

    [UnitTest]
    public async Task EnvelopeFactory_IdempotentRetry_ReturnsOriginalInstanceAndAuditsTouch()
    {
        var store = new VolatileOperationInstanceStore();
        var audit = new CancellationCheckingAuditLog();
        var factory = new OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var context = new OperationPolicyContext
        {
            PrincipalId = "gp-caller",
            IdempotencyKey = "gp-idem-1",
        };

        var first = await factory.CreateAcceptedAsync("control-plane.geoprocess", context);
        var retry = await factory.CreateAcceptedAsync("control-plane.geoprocess", context);

        retry.OperationInstanceId.Should().Be(first.OperationInstanceId);
        retry.CorrelationId.Should().Be(first.CorrelationId);
        retry.AuditId.Should().Be(first.AuditId);
        retry.EvidenceRefs.Should().ContainSingle(reference => reference.StartsWith("retry-audit:", StringComparison.Ordinal));
        audit.Events.Should().ContainSingle(entry =>
            entry.Action == "operation.retry" && entry.ResourceId == first.OperationInstanceId);
    }

    [UnitTest]
    public async Task LegacyAdapter_ExecutionIdentity_ReturnsQueuedEnvelope()
    {
        var actuator = Substitute.For<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor>();
        actuator.OperationClass.Returns(OperationClass.Deploy);
        actuator.ExecuteAsync(
                Arg.Any<Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("workflow-queued");
        var adapter = new LegacyGatewayOperationAdapter(actuator);

        var handle = await adapter.SubmitAsync(
            new OperationRequest
            {
                OperationId = adapter.OperationId,
                GatewayRequest = new Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest
                {
                    Kind = OperationClass.Deploy,
                },
            },
            new OperationPolicyContext
            {
                OperationInstanceId = "opinst-queued",
                CorrelationId = "corr-queued",
            });

        handle.Status.Should().Be(OperationHandleStatus.Queued);
        handle.JobId.Should().Be("workflow-queued");
    }

    [UnitTest]
    public async Task SubmitAsync_With_Deny_Policy_ShortCircuits_Executor_And_Returns_Denied_Handle()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(executor, new DenyAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // The guardrail seam: the executor's publish path is NEVER reached.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.Denied);
        handle.Result.Should().BeNull();
        handle.MetadataRevision.Should().BeNull();
        handle.ApprovalLane.Should().BeNull();
        handle.Reason.Should().Contain("blocked by policy");
    }

    [UnitTest]
    public async Task SubmitAsync_With_RequireApproval_And_No_Durable_Bridge_Fails_Closed()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(
            executor,
            new StubPolicyDecisionPoint(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                Reason = "operator approval required",
                ApprovalLane = "studio-publish-requests"
            }));

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // Guardrail seam: RequireApproval never reaches the executor.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.ProposalId.Should().BeNull();
        handle.AuditId.Should().StartWith("audit-dev-");
        handle.ApprovalLane.Should().Be("studio-publish-requests");
        handle.Reason.Should().Contain("durable proposal infrastructure is unavailable");
        handle.Result.Should().BeNull();
    }

    [UnitTest]
    public async Task SubmitAsync_With_Durable_Approval_Retains_Separate_Joined_Identities()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(
                Arg.Any<IOperationDescriptor>(),
                Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(),
                Arg.Any<PolicyDecision>(),
                Arg.Any<CancellationToken>())
            .Returns(new OperationApprovalBridgeResult
            {
                IsDurable = true,
                ProposalId = "proposal-123",
                AuditId = "audit-456",
                Reason = "Awaiting operator approval.",
            });
        var dispatcher = BuildDispatcher(
            executor,
            new StubPolicyDecisionPoint(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "studio-publish-requests",
            }),
            bridge);

        var handle = await dispatcher.SubmitAsync(
            BuildRequest(),
            new OperationPolicyContext { CorrelationId = "corr-789" },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        handle.OperationInstanceId.Should().StartWith("opinst-");
        handle.OperationInstanceId.Should().NotBe(handle.OperationId);
        handle.OperationInstanceId.Should().NotBe(handle.ProposalId);
        handle.HandleId.Should().Be(handle.OperationInstanceId);
        handle.ProposalId.Should().Be("proposal-123");
        handle.AuditId.Should().Be("audit-456");
        handle.CorrelationId.Should().Be("corr-789");
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_With_DryRunFirst_Policy_ShortCircuits_Executor_And_Returns_DryRunRequired_Handle()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(
            executor,
            new StubPolicyDecisionPoint(new PolicyDecision
            {
                Kind = PolicyDecisionKind.DryRunFirst,
                Reason = "preview required before commit"
            }));

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // Guardrail seam: DryRunFirst never reaches the executor — no side effect.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.DryRunRequired);
        handle.ApprovalLane.Should().BeNull();
        handle.Result.Should().BeNull();
        handle.Reason.Should().Contain("preview required");
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedDryRun_ValidatesWithoutActuation()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var dispatcher = BuildDispatcher(BuildExecutor(publishing), new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(
            BuildRequest() with { DryRun = true },
            new OperationPolicyContext(),
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.Reason.Should().Contain("no actuator");
        handle.Result!.Details["dryRun"].Should().Be(bool.TrueString);
        await publishing.Received(1).ValidateTableForPublishAsync(
            Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>());
        await publishing.DidNotReceiveWithAnyArgs()
            .PublishLayerAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task SubmitAsync_Flows_Descriptor_Policy_Metadata_Tier_And_Roles_Into_Decision_Input()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);

        // Return a non-Allow decision so the dispatcher short-circuits the executor after the
        // decision point has captured its inputs — the assertions below are on what reached the
        // decision point, not on execution.
        var capturing = new CapturingPolicyDecisionPoint(new PolicyDecision
        {
            Kind = PolicyDecisionKind.Deny,
            Reason = "captured"
        });
        var dispatcher = BuildDispatcher(executor, capturing);

        var context = new OperationPolicyContext
        {
            PrincipalId = "alice",
            Tier = "enterprise",
            Roles = ["operator", "publisher"]
        };

        await dispatcher.SubmitAsync(BuildRequest(), context, CancellationToken.None);

        // The descriptor's policy metadata reached the decision point...
        capturing.Descriptor.Should().NotBeNull();
        capturing.Descriptor!.OperationId.Should().Be("service.publish");
        capturing.Descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.ServiceScope);
        capturing.Descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        capturing.Descriptor.Policy.Determinism.Should().Be(OperationDeterminism.Deterministic);

        // ...alongside the caller's tier and role(s) for a tier/role-aware engine.
        capturing.Context.Should().NotBeNull();
        capturing.Context!.Tier.Should().Be("enterprise");
        capturing.Context.Roles.Should().BeEquivalentTo("operator", "publisher");
        capturing.Context.OperationInstanceId.Should().StartWith("opinst-");
        capturing.Context.OperationInstanceId.Should().NotBe(capturing.Descriptor.OperationId);
        capturing.Context.CorrelationId.Should().StartWith("corr-");
        capturing.Context.CorrelationId.Should().NotBe(capturing.Context.OperationInstanceId);
    }

    private static ServicePublishExecutor BuildExecutor(
        ILayerPublishingService publishing,
        IMetadataV2GraphProvider? graphProvider = null,
        IMcpNotificationPublisher? notifications = null)
    {
        publishing
            .ValidateTableForPublishAsync(
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
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        return new ServicePublishExecutor(
            publishing,
            resolver,
            graphProvider ?? Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System,
            notifications);
    }

    private static AdminOperateOperationExecutor BuildAdminExecutor(
        string operationId,
        HttpClient client,
        IAdminApiKeyStore? credentialStore = null)
    {
        var definition = AdminOperateOperationCatalog.Definitions.Should()
            .ContainSingle(item => item.OperationId == operationId).Subject;
        var descriptor = AdminOperateOperationCatalog.Descriptors.Should()
            .ContainSingle(item => item.OperationId == operationId).Subject;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("public.example.test");
        context.Connection.LocalPort = 8080;
        context.Request.Headers["X-API-Key"] = "secret";
        context.Request.Headers["X-Honua-Tenant"] = "tenant-a";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new AdminOperateOperationExecutor(
            definition,
            descriptor,
            factory,
            accessor,
            credentialStore ?? new InMemoryAdminApiKeyStore(TimeProvider.System),
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private static AdminOperateOperationExecutor BuildAccessAdminExecutor(
        string operationId,
        HttpClient client)
    {
        var definition = AdminAccessOperationCatalog.Definitions.Should()
            .ContainSingle(item => item.OperationId == operationId).Subject;
        var descriptor = AdminAccessOperationCatalog.Descriptors.Should()
            .ContainSingle(item => item.OperationId == operationId).Subject;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("public.example.test");
        context.Connection.LocalPort = 8080;
        context.Request.Headers["X-API-Key"] = "test-key";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return new AdminOperateOperationExecutor(
            definition,
            descriptor,
            factory,
            accessor,
            null,
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private static async Task AssertSecretOperationUnavailableAsync(string operationId)
    {
        var instanceStore = new VolatileOperationInstanceStore();
        var approvalBridge = Substitute.For<IOperationApprovalBridge>();
        var catalog = new OperationCatalog(
            [new AdminAccessOperationDescriptorProvider()],
            TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog,
            [],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            approvalBridge,
            instanceStore,
            new VolatileOperationAuditLog());
        var instanceId = $"opinst-gated-{operationId.Replace('.', '-')}";

        var exception = await Assert.ThrowsAsync<OperationUnavailableException>(() => dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = operationId,
                Parameters = new Dictionary<string, string?>
                {
                    ["secret"] = "must-not-appear-in-a-refusal"
                }
            },
            new OperationPolicyContext
            {
                OperationInstanceId = instanceId,
                ApprovedProposalId = "proposal-must-not-be-created"
            }));

        exception.OperationId.Should().Be(operationId);
        exception.Message.Should().Contain("#4187");
        exception.Message.Should().NotContain("must-not-appear-in-a-refusal");
        (await instanceStore.GetAsync(instanceId)).Should().BeNull();
        (await instanceStore.ListActiveAsync()).Should().BeEmpty();
        approvalBridge.ReceivedCalls().Should().BeEmpty();
    }

    private static OperationDispatcher BuildDispatcher(
        IOperationExecutor executor,
        IOperationPolicyDecisionPoint policy,
        IOperationApprovalBridge? approvalBridge = null)
    {
        var catalog = new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System);
        return new OperationDispatcher(catalog, [executor], policy, TimeProvider.System, approvalBridge);
    }

    private static OperationRequest BuildRequest()
        => new()
        {
            OperationId = "service.publish",
            ConnectionId = TestConnectionId,
            ServiceName = "default",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = "public",
                ["table"] = "parcels",
                ["layerName"] = "Parcels"
            }
        };

    /// <summary>
    /// Stub policy decision point that denies every operation, used to prove the dispatcher
    /// short-circuits the executor even though the production default is pass-through Allow.
    /// </summary>
    private sealed class DenyAllPolicyDecisionPoint : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PolicyDecision
            {
                Kind = PolicyDecisionKind.Deny,
                Reason = "blocked by policy (test stub)"
            });
    }

    /// <summary>
    /// Stub decision point that returns a caller-supplied fixed decision, used to prove each
    /// non-Allow outcome (RequireApproval / DryRunFirst) maps to its handle status.
    /// </summary>
    private sealed class StubPolicyDecisionPoint(PolicyDecision decision) : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    /// <summary>
    /// Stub decision point that captures the descriptor + context it was evaluated with, used
    /// to prove the descriptor's policy metadata and the caller's tier/role(s) flow into the
    /// decision input.
    /// </summary>
    private sealed class CapturingPolicyDecisionPoint(PolicyDecision decision) : IOperationPolicyDecisionPoint
    {
        public IOperationDescriptor? Descriptor { get; private set; }

        public OperationPolicyContext? Context { get; private set; }

        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            Descriptor = descriptor;
            Context = context;
            return Task.FromResult(decision);
        }
    }

    private sealed class CancelingAfterActuationExecutor(CancellationTokenSource requestCancellation)
        : IOperationExecutor
    {
        public string OperationId => "service.publish";

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            requestCancellation.Cancel();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OperationHandle
            {
                OperationInstanceId = context.OperationInstanceId!,
                OperationId = OperationId,
                CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CancelingDuringActuationExecutor(CancellationTokenSource requestCancellation)
        : IOperationExecutor
    {
        public string OperationId => "service.publish";

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            requestCancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CancelingDuringValidationExecutor(CancellationTokenSource requestCancellation)
        : IOperationExecutor
    {
        public string OperationId => "service.publish";

        public int SubmitCount { get; private set; }

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            requestCancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            throw new InvalidOperationException("Actuator must not run after canceled validation.");
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class QueuedExecutor : IOperationExecutor
    {
        public string OperationId => "service.publish";

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OperationHandle
            {
                OperationInstanceId = context.OperationInstanceId!,
                OperationId = OperationId,
                CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                JobId = "job-queued",
            });
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CancellationCheckingAuditLog : IAuditLog
    {
        public int CanceledWriteCount { get; private set; }

        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            if (cancellationToken.IsCancellationRequested)
            {
                CanceledWriteCount++;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult<string?>($"audit-test-{Guid.NewGuid():N}");
        }
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        public HttpRequestHeaders? Headers { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Headers = request.Headers;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CapturingOperationHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}
