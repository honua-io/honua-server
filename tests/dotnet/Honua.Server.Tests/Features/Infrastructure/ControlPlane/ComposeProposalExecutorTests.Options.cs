// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class ComposeProposalExecutorTests
{
    [UnitTheory]
    [InlineData(ComposeExecutorProtocol.OverrideFileName, ComposeExecutorCodes.ExecutorUnconfigured)]
    [InlineData("compose.yaml::customer.yaml", ComposeExecutorCodes.ScopeViolation)]
    [InlineData("compose.yaml:", ComposeExecutorCodes.ScopeViolation)]
    [InlineData("../outside.yaml", ComposeExecutorCodes.ScopeViolation)]
    [InlineData("/outside.yaml", ComposeExecutorCodes.ScopeViolation)]
    [InlineData("compose.yaml:missing.yaml", ComposeExecutorCodes.ExecutorUnconfigured)]
    [InlineData("compose.yaml:" + ComposeExecutorProtocol.OverrideFileName, null)]
    public void ValidateForProcess_ProjectFileList_EnforcesProjectScope(string files, string? expectedCode)
    {
        var root = Path.Join(Path.GetTempPath(), "honua-compose-options", Guid.NewGuid().ToString("N"));
        try
        {
            var options = ValidOptions(root, Digest('a'));
            options.ComposeFileList = files;

            Assert.Equal(expectedCode, options.ValidateForProcess()?.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTheory]
    [InlineData("compose.yaml", "missing-array.yaml", "missing-scalar.yaml")]
    [InlineData("", "compose.yaml", "missing-scalar.yaml")]
    [InlineData("", "", "compose.yaml")]
    public void ComposeFilePaths_ConfigurationPrecedence_UsesTheFirstConfiguredSource(
        string projectFiles, string legacyFiles, string legacyFile)
    {
        var root = Path.Join(Path.GetTempPath(), "honua-compose-options", Guid.NewGuid().ToString("N"));
        try
        {
            var options = ValidOptions(root, Digest('a'));
            options.ComposeFileList = projectFiles;
            options.ComposeFiles = string.IsNullOrEmpty(legacyFiles) ? [] : [legacyFiles];
            options.ComposeFile = legacyFile;

            Assert.Null(options.ValidateForProcess());
            Assert.Equal([Path.Combine(options.ProjectDirectory, "compose.yaml")], options.ComposeFilePaths());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
