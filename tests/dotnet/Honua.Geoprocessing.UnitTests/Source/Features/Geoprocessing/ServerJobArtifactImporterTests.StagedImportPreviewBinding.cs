// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed partial class ServerJobArtifactImporterTests
{
    [UnitTest]
    public async Task StageImport_PreviewExpiredBeforeProposal_IsRefused()
    {
        var harness = await OperationHarnessAsync();
        var preview = await harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
            harness.Context(), CancellationToken.None);
        harness.Time.Advance(StagedImportOperation.ApprovalWindow);
        var request = harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter]));

        var act = () => harness.Import.PrepareAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_ProposalKeepsPreviewExpiry_InsteadOfRenewingIt()
    {
        var harness = await OperationHarnessAsync();
        var preview = await harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
            harness.Context(), CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromMinutes(20));
        var prepared = await harness.Import.PrepareAsync(harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter])),
            harness.Context(), CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromMinutes(10));

        (await harness.Import.ValidateAsync(prepared, CancellationToken.None)).IsValid.Should().BeFalse();
    }

    [UnitTest]
    public async Task StageImport_NewPreviewDoesNotRenewAnOlderIdentity()
    {
        var harness = await OperationHarnessAsync();
        var request = harness.Request(StagedImportOperation.PreviewOperationId);
        var first = await harness.Preview.SubmitAsync(request, harness.Context(), CancellationToken.None);
        harness.Time.Advance(StagedImportOperation.ApprovalWindow);
        var second = await harness.Preview.SubmitAsync(request, harness.Context(), CancellationToken.None);
        var key = StagedImportOperation.PreviewFingerprintParameter;

        second.Result!.Details[key].Should().NotBe(first.Result!.Details[key]);
        var act = () => harness.Import.PrepareAsync(harness.Request(StagedImportOperation.ImportOperationId,
            (key, first.Result.Details[key])), harness.Context(), CancellationToken.None);
        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await harness.Import.PrepareAsync(harness.Request(StagedImportOperation.ImportOperationId,
            (key, second.Result.Details[key])), harness.Context(), CancellationToken.None);
    }

    [UnitTheory]
    [InlineData("tenant")]
    [InlineData("requester")]
    public async Task StageImport_PreviewCannotBeProposedUnderAnotherScope(string change)
    {
        var harness = await OperationHarnessAsync();
        var preview = await harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
            harness.Context(), CancellationToken.None);
        var context = change == "tenant" ? harness.Context() with { TenantId = "tenant-b" }
            : harness.Context() with { PrincipalId = "actor-2" };
        var request = harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter]));

        var act = () => new StagedImportExecutor(harness.Services).PrepareAsync(request, context, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_ChangedCrsAfterPreview_IsRefused()
    {
        var harness = await OperationHarnessAsync();
        var preview = await harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
            harness.Context(), CancellationToken.None);
        var request = harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter]),
            ("targetSrid", "3857"));

        var act = () => harness.Import.PrepareAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

}
