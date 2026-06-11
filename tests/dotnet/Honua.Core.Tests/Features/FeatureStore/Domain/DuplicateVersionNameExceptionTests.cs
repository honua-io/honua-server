// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit tests for the <see cref="DuplicateVersionNameException"/> domain exception.
/// </summary>
public sealed class DuplicateVersionNameExceptionTests
{
    [UnitTest]
    public void Ctor_VersionName_SetsVersionNameProperty()
    {
        var ex = new DuplicateVersionNameException("admin.my-branch");

        ex.VersionName.Should().Be("admin.my-branch");
    }

    [UnitTest]
    public void Ctor_VersionName_SetsDescriptiveMessage()
    {
        var ex = new DuplicateVersionNameException("admin.my-branch");

        ex.Message.Should().Contain("admin.my-branch");
    }

    [UnitTest]
    public void Ctor_WithInnerException_SetsInnerExceptionAndVersionName()
    {
        var inner = new InvalidOperationException("storage conflict");
        var ex = new DuplicateVersionNameException("owner.branch", inner);

        ex.VersionName.Should().Be("owner.branch");
        ex.InnerException.Should().BeSameAs(inner);
        ex.Message.Should().Contain("owner.branch");
    }

    [UnitTest]
    public void Ctor_WithInnerException_PreservesInnerExceptionMessage()
    {
        var inner = new InvalidOperationException("unique constraint violation");
        var ex = new DuplicateVersionNameException("test.version", inner);

        ex.InnerException!.Message.Should().Be("unique constraint violation");
    }
}
