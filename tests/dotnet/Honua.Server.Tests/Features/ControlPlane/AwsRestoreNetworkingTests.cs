// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.RDS;
using Amazon.RDS.Model;
using Honua.Cloud.Aws.Features.ControlPlane;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.ControlPlane;

public sealed class AwsRestoreNetworkingTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RestoreNew_PassesAuthorizedSecurityGroups_ToProvider()
    {
        var rds = Substitute.For<IAmazonRDS>();
        rds.RestoreDBInstanceFromDBSnapshotAsync(Arg.Any<RestoreDBInstanceFromDBSnapshotRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreDBInstanceFromDBSnapshotResponse());
        rds.DescribeDBInstancesAsync(Arg.Any<DescribeDBInstancesRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DescribeDBInstancesResponse
            {
                DBInstances = [new DBInstance { DBInstanceStatus = "available", Endpoint = new Endpoint { Address = "restored.internal" } }]
            });
        using var client = new AwsRdsCellRestoreClient(rds);
        var result = await client.RestoreNewAsync(new AwsRdsRestoreCommand(
            "honua-cert-snapshot", "honua-cert-restore-0123456789abcdef", "honua-cert-", "honua-cert-subnet", "cert", 10,
            ["sg-01234567", "sg-0123456789abcdef0"]), CancellationToken.None);
        Assert.True(result.Available);
        await rds.Received(1).RestoreDBInstanceFromDBSnapshotAsync(
            Arg.Is<RestoreDBInstanceFromDBSnapshotRequest>(request => request.VpcSecurityGroupIds.SequenceEqual(
                new[] { "sg-01234567", "sg-0123456789abcdef0" })), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RestoreNew_ExistingInstance_DoesNotTreatItAsVerifiedBackup()
    {
        var rds = Substitute.For<IAmazonRDS>();
        rds.RestoreDBInstanceFromDBSnapshotAsync(Arg.Any<RestoreDBInstanceFromDBSnapshotRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RestoreDBInstanceFromDBSnapshotResponse>(new AmazonRDSException("already exists") { ErrorCode = "DBInstanceAlreadyExists" }));
        using var client = new AwsRdsCellRestoreClient(rds);
        var result = await client.RestoreNewAsync(new AwsRdsRestoreCommand(
            "honua-cert-snapshot", "honua-cert-restore-0123456789abcdef", "honua-cert-", "honua-cert-subnet", "cert", 10,
            ["sg-01234567"]), CancellationToken.None);
        Assert.False(result.Available);
        await rds.DidNotReceive().DescribeDBInstancesAsync(Arg.Any<DescribeDBInstancesRequest>(), Arg.Any<CancellationToken>());
    }
}
