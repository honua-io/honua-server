// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class AdminHttpOperationDefinitionTests
{
    public static IEnumerable<object[]> Catalogs =>
        new[] { "api", "operate", "access", "connect" }.Select(name => new object[] { name });

    [UnitTheory]
    [MemberData(nameof(Catalogs))]
    public void Definitions_PublishedDescriptors_PreserveCanonicalPolicy(string catalog)
    {
        var (definitions, descriptors) = GetCatalog(catalog);
        foreach (var item in definitions)
        {
            var definition = item.Should().BeAssignableTo<IAdminHttpOperationDefinition>().Subject;
            var descriptor = descriptors.Should().ContainSingle(value => value.OperationId == definition.OperationId).Subject;
            var approval = definition.ApprovalModel ?? (definition.SideEffect == OperationSideEffectClass.ReadOnly
                ? OperationApprovalModel.None : OperationApprovalModel.OperatorGate);

            descriptor.Title.Should().Be(definition.Title);
            descriptor.ApprovalModel.Should().Be(approval, definition.OperationId);
            descriptor.Policy.SideEffectClass.Should().Be(definition.SideEffect, definition.OperationId);
            descriptor.Policy.BlastRadiusClass.Should().Be(definition.BlastRadius, definition.OperationId);
            descriptor.Policy.SupportsDryRun.Should().Be(definition.SupportsDryRun, definition.OperationId);
        }
    }

    [UnitTheory]
    [InlineData("admin.connections.test-draft")]
    [InlineData("admin.connections.test")]
    [InlineData("admin.connections.tables.validate")]
    [InlineData("admin.import.preview")]
    [InlineData("admin.import.preview-url")]
    public void Definition_ReadOnlyPost_PreservesPolicyAndDryRunMethod(string operationId)
    {
        var item = AdminConnectImportOperationCatalog.Definitions.Single(value => value.OperationId == operationId);
        var definition = ((object)item).Should().BeAssignableTo<IAdminHttpOperationDefinition>().Subject;

        definition.Method.Should().Be(HttpMethod.Post);
        definition.SideEffect.Should().Be(OperationSideEffectClass.ReadOnly);
        definition.ApprovalModel.Should().Be(OperationApprovalModel.None);
        definition.SupportsDryRun.Should().BeTrue();
        (definition.DryRunMethod ?? definition.Method).Should().Be(HttpMethod.Post);
        definition.DryRunPath.Should().Be(definition.Path);
    }

    [UnitTheory]
    [InlineData("admin.connections.create")]
    [InlineData("admin.import.upload-url")]
    [InlineData("admin.import.upload-object")]
    public void Definition_DirectOperatorJourney_PreservesApprovalException(string operationId)
    {
        var item = AdminConnectImportOperationCatalog.Definitions.Single(value => value.OperationId == operationId);
        var definition = ((object)item).Should().BeAssignableTo<IAdminHttpOperationDefinition>().Subject;

        definition.SideEffect.Should().NotBe(OperationSideEffectClass.ReadOnly);
        definition.ApprovalModel.Should().Be(OperationApprovalModel.None);
    }

    [UnitTheory]
    [MemberData(nameof(Catalogs))]
    public void Descriptors_RecoveryNotImplemented_ExplicitlyDescribeUnavailableCompensation(string catalog)
    {
        var (_, descriptors) = GetCatalog(catalog);
        var snapshot = new OperationCatalogSnapshot
        {
            CatalogVersion = "test",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Operations = descriptors
        };
        var published = JsonSerializer.SerializeToElement(snapshot, OperationsJsonContext.Default.OperationCatalogSnapshot);
        foreach (var operation in published.GetProperty("operations").EnumerateArray())
        {
            var descriptor = descriptors.Single(item => item.OperationId == operation.GetProperty("operationId").GetString());
            operation.TryGetProperty("compensation", out var compensation).Should().BeTrue(descriptor.OperationId);
            var readOnly = descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly;
            compensation.GetProperty("availability").GetString().Should().Be(readOnly ? "NotApplicable" : "Unavailable");
            if (!readOnly)
            {
                compensation.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace(descriptor.OperationId);
            }
        }
    }

    private static (IEnumerable<object> Definitions, IReadOnlyList<OperationDescriptor> Descriptors) GetCatalog(string catalog) =>
        catalog switch
        {
            "api" => (AdminApiOperationCatalog.Definitions, AdminApiOperationCatalog.Descriptors),
            "operate" => (AdminOperateOperationCatalog.Definitions, AdminOperateOperationCatalog.Descriptors),
            "access" => (AdminAccessOperationCatalog.Definitions, AdminAccessOperationCatalog.Descriptors),
            "connect" => (AdminConnectImportOperationCatalog.Definitions, AdminConnectImportOperationCatalog.Descriptors),
            _ => throw new ArgumentOutOfRangeException(nameof(catalog))
        };
}
