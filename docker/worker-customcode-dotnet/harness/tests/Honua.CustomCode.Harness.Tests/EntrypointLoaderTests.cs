// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.CustomCode.Harness;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

/// <summary>A valid tool type used for entrypoint type-loading tests.</summary>
public sealed class FakeBufferTool : IGeoprocessingTool
{
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
        => Task.FromResult(GpResult.Succeeded("ok"));
}

/// <summary>A type that does NOT implement the contract.</summary>
public sealed class NotATool
{
}

public sealed class EntrypointLoaderTests
{
    private static Assembly ThisAssembly => typeof(EntrypointLoaderTests).Assembly;

    [Fact]
    public void Load_ResolvesAndActivates_ValidToolType()
    {
        var loader = new EntrypointLoader(_ => ThisAssembly);

        var tool = loader.Load("ignored", $"FakeAsm::{typeof(FakeBufferTool).FullName}");

        tool.Should().BeOfType<FakeBufferTool>();
    }

    [Fact]
    public void Load_TypeNotImplementingContract_Throws()
    {
        var loader = new EntrypointLoader(_ => ThisAssembly);

        var act = () => loader.Load("ignored", $"FakeAsm::{typeof(NotATool).FullName}");

        act.Should().Throw<EntrypointException>().WithMessage("*does not implement IGeoprocessingTool*");
    }

    [Fact]
    public void Load_MissingType_Throws()
    {
        var loader = new EntrypointLoader(_ => ThisAssembly);

        var act = () => loader.Load("ignored", "FakeAsm::No.Such.Type");

        act.Should().Throw<EntrypointException>().WithMessage("*has no type*");
    }

    [Theory]
    [InlineData("NoSeparator")]
    [InlineData("Asm::")]
    [InlineData("::Type")]
    public void Load_MalformedEntrypoint_Throws(string entrypoint)
    {
        var loader = new EntrypointLoader(_ => ThisAssembly);

        var act = () => loader.Load("ignored", entrypoint);

        act.Should().Throw<EntrypointException>();
    }
}
