// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalRemoteInputScopeTests
{
    [UnitTest]
    public void RemoteInputScope_TrustsOnlyExactDeclaredObjectStorePaths()
    {
        using var scope = GdalRemoteInputScope.Trust("/vsis3/bucket/x.nc", "/vsiaz/container/input.tif");

        GdalRemoteInputScope.Classify(new[] { "-json", "/vsis3/bucket/x.nc" })
            .Should().Be(new GdalRemoteInputs(Remote: true, S3: true, Azure: false));
        GdalRemoteInputScope.Classify(new[] { "/vsiaz/container/input.tif" })
            .Should().Be(new GdalRemoteInputs(Remote: true, S3: false, Azure: true));
        GdalRemoteInputScope.Classify(new[] { "-json", "/scratch/op/input.tif" })
            .Should().Be(GdalRemoteInputs.None);

        // Substrings, case variants and undeclared paths never count.
        GdalRemoteInputScope.Classify(new[] { "-nln", "layer/vsis3/bucket/x.nc" })
            .Should().Be(GdalRemoteInputs.None);
        GdalRemoteInputScope.Classify(new[] { "/VSIS3/bucket/x.nc" })
            .Should().Be(GdalRemoteInputs.None);
        GdalRemoteInputScope.Classify(new[] { "/vsis3/other-bucket/x.nc" })
            .Should().Be(GdalRemoteInputs.None);
    }

    [UnitTest]
    public void RemoteInputScope_DerivedArgumentMustStartWithTheTrustedZarrRoot()
    {
        const string root = "/vsis3/bucket/cube.zarr";
        const string embedded = "ZARR:\"/vsis3/other/cube.zarr\":/\"/vsis3/bucket/cube.zarr\"";

        using var rootScope = GdalRemoteInputScope.Trust(root);
        using (GdalRemoteInputScope.TrustDerived([embedded, root + "/../x"], root))
        {
            GdalRemoteInputScope.Classify(new[] { embedded }).Should().Be(GdalRemoteInputs.None);
            GdalRemoteInputScope.Classify(new[] { root + "/../x" }).Should().Be(GdalRemoteInputs.None);
        }
    }

    [UnitTest]
    public void RemoteInputScope_DerivedArgumentNeedsATrustedRoot()
    {
        const string root = "/vsis3/bucket/cube.zarr";
        const string subdataset = "ZARR:\"/vsis3/bucket/cube.zarr\":/temperature";

        using (GdalRemoteInputScope.TrustDerived([subdataset], root))
        {
            GdalRemoteInputScope.Classify(new[] { subdataset }).Should().Be(GdalRemoteInputs.None);
        }

        using var rootScope = GdalRemoteInputScope.Trust(root);
        using (GdalRemoteInputScope.TrustDerived(["ZARR:\"/vsis3/other/cube.zarr\":/t"], root))
        {
            GdalRemoteInputScope.Classify(new[] { "ZARR:\"/vsis3/other/cube.zarr\":/t" })
                .Should().Be(GdalRemoteInputs.None);
        }

        using (GdalRemoteInputScope.TrustDerived([subdataset], root))
        {
            GdalRemoteInputScope.Classify(new[] { subdataset })
                .Should().Be(new GdalRemoteInputs(Remote: true, S3: true, Azure: false));
        }

        GdalRemoteInputScope.Classify(new[] { subdataset }).Should().Be(GdalRemoteInputs.None);
    }

    [UnitTest]
    public async Task RemoteInputScope_FlowsToCalleesAndRestoresOnDispose()
    {
        GdalRemoteInputScope.Classify(new[] { "/vsis3/bucket/a.tif" }).Remote.Should().BeFalse();

        using (GdalRemoteInputScope.Trust("/vsis3/bucket/a.tif", null, "/scratch/local.tif"))
        {
            await Task.Yield();
            GdalRemoteInputScope.Classify(new[] { "/vsis3/bucket/a.tif" }).S3.Should().BeTrue();
            GdalRemoteInputScope.Classify(new[] { "/scratch/local.tif" }).Remote.Should().BeFalse();

            using (GdalRemoteInputScope.Trust("/vsiaz/container/b.tif"))
            {
                GdalRemoteInputScope.Classify(new[] { "/vsis3/bucket/a.tif", "/vsiaz/container/b.tif" })
                    .Should().Be(new GdalRemoteInputs(Remote: true, S3: true, Azure: true));
            }

            GdalRemoteInputScope.Classify(new[] { "/vsiaz/container/b.tif" }).Remote.Should().BeFalse();
        }

        GdalRemoteInputScope.Classify(new[] { "/vsis3/bucket/a.tif" }).Remote.Should().BeFalse();
    }
}
