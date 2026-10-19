// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public sealed class OgcFeatureEditFailureMapperTests
{
    [UnitTheory]
    [InlineData("Operation rolled back.", 1000, 424)]
    [InlineData("Feature not found.", 1000, 404)]
    [InlineData("The operation conflicted with existing data.", 1000, 409)]
    [InlineData("Invalid feature data.", 1000, 400)]
    [InlineData("Geometry is required for create operation on a spatial layer", 1000, 400)]
    [InlineData("Create failed.", 1000, 500)]
    [InlineData("Something else.", 422, 400)]
    public void DetermineStatus_ClassifiesSafeWriterMessages(string message, int code, int expected)
    {
        var status = OgcFeatureEditFailureMapper.DetermineStatus(
            EditOperationResult.Failure(message, code));

        status.Should().Be(expected);
    }

    [UnitTest]
    public void ToResult_ConflictMessage_ReturnsConflict()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/ogc/features/collections/test/items";

        var result = OgcFeatureEditFailureMapper.ToResult(
            context,
            EditOperationResult.Failure("The operation conflicted with existing data."),
            "An error occurred while creating the feature.");

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }
}
