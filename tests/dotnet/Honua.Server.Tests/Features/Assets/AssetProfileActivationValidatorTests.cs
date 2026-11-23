// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Forms.Packages;
using Honua.Server.Features.Assets;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Assets;

public sealed class AssetProfileActivationValidatorTests
{
    [UnitTest]
    public async Task ValidateAsync_AssetIdEndingInLineFeed_IsRejectedBeforeQuerying()
    {
        var features = Substitute.For<IFeatureReader>();
        var validator = new AssetProfileActivationValidator(features);
        var target = new FormTargetMetadataResolution(null, null, null, 7);

        var errors = await validator.ValidateAsync(
            target, new AssetProfileFieldMap { AssetId = "sign_id\n" }, CancellationToken.None);

        errors.Should().ContainSingle().Which.Field.Should().Be("fields.assetId");
        features.ReceivedCalls().Should().BeEmpty();
    }
}
