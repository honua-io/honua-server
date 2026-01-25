// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Honua.Postgres.Features.FeatureStore.Services;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureDataAccessTimeoutTests
{
    private static readonly OpCode[] _singleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] _multiByteOpCodes = new OpCode[0x100];

    static FeatureDataAccessTimeoutTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = (ushort)opCode.Value;
            if (value < 0x100)
            {
                _singleByteOpCodes[value] = opCode;
            }
            else if ((value & 0xFF00) == 0xFE00)
            {
                _multiByteOpCodes[value & 0xFF] = opCode;
            }
        }
    }

    [Fact]
    public void GetTemporalExtentAsync_AppliesCommandTimeout()
    {
        var method = typeof(FeatureDataAccess)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => m.Name == "GetTemporalExtentAsync" && m.GetParameters().Length == 3);

        var applyTimeout = typeof(FeatureDataAccess).GetMethod(
            "ApplyCommandTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);

        applyTimeout.Should().NotBeNull();

        var stateMachineAttribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        stateMachineAttribute.Should().NotBeNull();

        var moveNext = stateMachineAttribute!.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance);

        moveNext.Should().NotBeNull();

        var il = moveNext!.GetMethodBody()?.GetILAsByteArray();
        il.Should().NotBeNull();

        ContainsMethodCall(il!, moveNext.Module, applyTimeout!).Should().BeTrue();
    }

    private static bool ContainsMethodCall(byte[] il, Module module, MethodInfo target)
    {
        for (var index = 0; index < il.Length;)
        {
            var opCode = ReadOpCode(il, ref index);

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, index);
                index += 4;

                try
                {
                    var resolved = module.ResolveMethod(token);
                    if (resolved == target)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore invalid metadata tokens while scanning.
                }

                continue;
            }

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                var count = BitConverter.ToInt32(il, index);
                index += 4 + (count * 4);
                continue;
            }

            index += GetOperandSize(opCode.OperandType);
        }

        return false;
    }

    private static OpCode ReadOpCode(byte[] il, ref int index)
    {
        var code = il[index++];
        if (code != 0xFE)
        {
            return _singleByteOpCodes[code];
        }

        var code2 = il[index++];
        return _multiByteOpCodes[code2];
    }

    private static int GetOperandSize(OperandType operandType)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineType => 4,
            OperandType.InlineString => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineR => 8,
            OperandType.ShortInlineR => 4,
            _ => 0
        };
}
