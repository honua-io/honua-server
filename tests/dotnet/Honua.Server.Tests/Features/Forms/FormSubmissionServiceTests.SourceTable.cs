// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Server.Features.Forms;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Forms;

// A target served from a source table is refused before any read of the target row.
public sealed partial class FormSubmissionServiceTests
{
    [UnitTheory]
    [InlineData(FormSubmissionOperations.Update)]
    [InlineData(FormSubmissionOperations.Delete)]
    public async Task SubmitAsync_TargetServedFromSourceTable_IsRefusedBeforeOwnerPolicyRead(string operation)
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(allowedOperations: [operation]);
        var writer = new FakeFeatureWriter();
        var reader = ReaderWithOwner("another-user");
        var service = CreateServiceForSourceTableTarget(store, writer, reader);
        var context = CreateContext(CreateSubmission($"source-{operation}", operation, targetFeatureId: 101), requestServices);

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed, "the owner read would not find the row");
        ReadResponseBody(context).Retry!.Retryable.Should().BeFalse();
        writer.ApplyEditsCalls.Should().Be(0);
        store.DeleteCalls.Should().Be(1);
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    private static FormSubmissionService CreateServiceForSourceTableTarget(
        FakeFormPackageStore store,
        FakeFeatureWriter writer,
        IFeatureReader featureReader)
    {
        var resolver = new SourceTableTargetResolver(new FormTargetMetadataResolver(CreateMetadataProvider(EnabledOwnerPolicy)));
        return new FormSubmissionService(
            store,
            new FormPackageValidator(resolver, Options.Create(CreateLimitsOptions())),
            resolver,
            new PassThroughEditProcessor(),
            featureReader,
            writer,
            new RecordingAttachmentStore(),
            Options.Create(CreateLimitsOptions()),
            Options.Create(new FileUploadSecurityOptions()),
            NullAuditLog.Instance,
            NullLogger<FormSubmissionService>.Instance);
    }

    // Reports the real target as served from a source table.
    private sealed class SourceTableTargetResolver(IFormTargetMetadataResolver inner) : IFormTargetMetadataResolver
    {
        public async Task<FormTargetMetadataResolution> ResolveAsync(
            FormTargetDefinition? target,
            CancellationToken cancellationToken = default)
            => (await inner.ResolveAsync(target, cancellationToken)) with { ServedFromSourceTable = true };
    }
}
