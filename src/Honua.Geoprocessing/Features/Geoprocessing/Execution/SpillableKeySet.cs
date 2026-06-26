// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Bounded-memory "have I seen this key?" set for the streaming dedup transform. Each
/// caller key (which can be a long normalized-WKT string) is reduced to a fixed 16-byte
/// SHA-256 digest, so per-key memory is constant regardless of key length. The digests
/// live in an in-memory <see cref="HashSet{T}"/> until they cross
/// <see cref="_inMemoryDigestLimit"/>, at which point the set spills to a temp file and
/// continues with a bounded recent-window cache plus on-disk membership checks — peak
/// memory stays bounded by the window, not by the cardinality of the input.
///
/// <para>
/// Collision note: a 128-bit digest makes an accidental false-positive (two distinct keys
/// hashing equal, which would wrongly drop a feature) astronomically unlikely for any
/// realistic ETL volume (~10^18 keys for a 1% chance). This is the documented bound: dedup
/// is exact up to the 128-bit digest, not byte-for-byte, in exchange for constant per-key
/// memory and a spill ceiling. Determinism is preserved: the same input always produces the
/// same first-wins output.
/// </para>
/// </summary>
internal sealed class SpillableKeySet : IDisposable
{
    private readonly HashSet<Digest> _inMemory = new();
    private readonly long _inMemoryDigestLimit;
    private readonly int _windowLimit;

    // Once spilled, recent digests are kept in a bounded window for fast negative checks;
    // the authoritative membership is the on-disk sorted-append file scanned per probe miss.
    private readonly LinkedList<Digest> _windowOrder = new();
    private readonly HashSet<Digest> _window = new();
    private FileStream? _spillFile;
    private string? _spillPath;
    private bool _spilled;

    public SpillableKeySet(long inMemoryDigestLimit = 2_000_000, int windowLimit = 1_000_000)
    {
        _inMemoryDigestLimit = inMemoryDigestLimit;
        _windowLimit = windowLimit;
    }

    /// <summary>
    /// Returns <c>true</c> the first time a key is seen (caller should emit the feature),
    /// <c>false</c> for a duplicate (caller should drop it).
    /// </summary>
    public bool Add(string key)
    {
        var digest = Digest.From(key);

        if (!_spilled)
        {
            var added = _inMemory.Add(digest);
            if (added && _inMemory.Count >= _inMemoryDigestLimit)
            {
                SpillInMemoryToDisk();
            }

            return added;
        }

        // Spilled mode: check the recent window first (cheap), then scan the spill file.
        if (_window.Contains(digest))
        {
            return false;
        }

        if (ExistsOnDisk(digest))
        {
            return false;
        }

        AppendToDisk(digest);
        AddToWindow(digest);
        return true;
    }

    private void SpillInMemoryToDisk()
    {
        _spillPath = Path.Combine(Path.GetTempPath(), $"honua-dedup-{Guid.NewGuid():N}.keys");
        _spillFile = new FileStream(_spillPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Span<byte> scratch = stackalloc byte[Digest.Size];
        foreach (var digest in _inMemory)
        {
            digest.WriteTo(scratch);
            _spillFile.Write(scratch);
            AddToWindow(digest);
        }

        _spillFile.Flush();
        _inMemory.Clear();
        _inMemory.TrimExcess();
        _spilled = true;
    }

    private bool ExistsOnDisk(Digest digest)
    {
        if (_spillFile is null)
        {
            return false;
        }

        _spillFile.Flush();
        _spillFile.Seek(0, SeekOrigin.Begin);

        Span<byte> buffer = stackalloc byte[Digest.Size];
        int read;
        while ((read = ReadFull(_spillFile, buffer)) == Digest.Size)
        {
            if (Digest.From(buffer).Equals(digest))
            {
                return true;
            }
        }

        _ = read;
        return false;
    }

    private void AppendToDisk(Digest digest)
    {
        if (_spillFile is null)
        {
            return;
        }

        _spillFile.Seek(0, SeekOrigin.End);
        Span<byte> scratch = stackalloc byte[Digest.Size];
        digest.WriteTo(scratch);
        _spillFile.Write(scratch);
        _spillFile.Flush();
    }

    private void AddToWindow(Digest digest)
    {
        if (_window.Add(digest))
        {
            _windowOrder.AddLast(digest);
            if (_window.Count > _windowLimit)
            {
                var oldest = _windowOrder.First!.Value;
                _windowOrder.RemoveFirst();
                _window.Remove(oldest);
            }
        }
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Dispose()
    {
        _spillFile?.Dispose();
        if (_spillPath is not null && File.Exists(_spillPath))
        {
            try
            {
                File.Delete(_spillPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp spill file.
            }
        }
    }

    private readonly struct Digest : IEquatable<Digest>
    {
        public const int Size = 16;

        private readonly ulong _hi;
        private readonly ulong _lo;

        private Digest(ulong hi, ulong lo)
        {
            _hi = hi;
            _lo = lo;
        }

        public static Digest From(string key)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
            return From(hash);
        }

        public static Digest From(ReadOnlySpan<byte> bytes)
            => new(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)));

        public void WriteTo(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], _hi);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), _lo);
        }

        public bool Equals(Digest other) => _hi == other._hi && _lo == other._lo;

        public override bool Equals(object? obj) => obj is Digest other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_hi, _lo);
    }
}
