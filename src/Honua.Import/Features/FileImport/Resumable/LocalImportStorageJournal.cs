// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.FileStorage;
using Microsoft.Win32.SafeHandles;

namespace Honua.Import.FileImport.Resumable;

// Private control-plane storage, separate from data and native scratch volumes.
// The coordinator must acquire the domain fence before any journal call and must
// prove physical absence before retiring an entry. This class grants no authority
// and does not initialize Redis or enable an import deployment by itself.
internal sealed partial class LocalImportStorageJournal
{
    // Fits maximum canonical scope lengths and the physical location even when
    // JSON escapes every UTF-16 character; do not narrow actor/tenant limits.
    internal const int PageBytes = 32 * 1024;
    private const int HeaderBytes = 32;
    private const int HashBytes = 32;
    private const ulong Magic = 0x314C4E4A41554E48;
    private const byte HeaderKind = 1;
    private const byte EntryKind = 2;
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    private readonly string _directory;
    private readonly string _path;
    private readonly string _domainId;
    private readonly int _capacity;
    private readonly LocalStorageDomainLock _journalLock;

    internal LocalImportStorageJournal(string directory, string domainId, int capacity)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Local import journals require Linux durability.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        if (!Path.IsPathFullyQualified(directory) || domainId.Length > 256 || domainId.Any(char.IsControl))
            throw new ArgumentException("The journal requires an absolute private root and a bounded host domain identity.");
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1000000);
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RejectExistingLinks(_directory);
        var parent = Path.GetDirectoryName(_directory) ?? throw new ArgumentException("The filesystem root cannot be an import journal.", nameof(directory));
        if ((File.GetAttributes(parent) & FileAttributes.Directory) == 0)
            throw new IOException("The journal requires an existing host-provisioned parent directory.");
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(_directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The import journal directory must be private to its host identity.");
        LocalStorageDurability.FlushDirectoryTree(_directory, parent);
        _domainId = domainId;
        _capacity = capacity;
        _path = Path.Join(_directory, "reservations.bin");
        // Separate serialization lock: a normal writer already holds the shared
        // domain fence. Taking its exclusive form here would be a lock upgrade.
        _journalLock = new LocalStorageDomainLock(_directory, "journal.lock");
    }

    internal long AllocatedBytes => checked((2L + (2L * _capacity)) * PageBytes);
    internal string DirectoryPath => _directory;

    internal async Task<LocalImportStorageJournalEntry?> ReadEntryAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await using var held = await _journalLock.EnterAsync(exclusive: false, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        _ = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        var found = await FindAsync(file, fileId, cancellationToken).ConfigureAwait(false);
        return found.Entry is { } entry ? new(entry.Reservation, entry.State, found.Slot) : null;
    }

    // Only fresh-domain bootstrap may call this, under the exclusive domain fence
    // after a raw physical census proves the configured data roots empty.
    internal async Task CreateEmptyAsync(string epoch, string? rootsSha256 = null, CancellationToken cancellationToken = default)
    {
        ValidateEpoch(epoch);
        if (!IsRootFingerprint(rootsSha256)) throw new ArgumentException("Invalid root binding fingerprint.", nameof(rootsSha256));
        await using var held = await _journalLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var error = Allocate(file.SafeFileHandle, 0, AllocatedBytes);
        if (error != 0) throw new IOException($"The import journal headroom could not be reserved (errno {error}).");
        file.SetLength(AllocatedBytes);
        await WritePageAsync(file, 0, null, HeaderKind,
            JsonSerializer.SerializeToUtf8Bytes(new Header(_domainId, _capacity, epoch, Guid.NewGuid().ToString("N"), rootsSha256), JournalJsonContext.Default.Header),
            cancellationToken).ConfigureAwait(false);
        LocalStorageDurability.FlushDirectoryTree(_directory, _directory);
    }

    internal async Task<string> ReadEpochAsync(CancellationToken cancellationToken = default)
        => (await ReadIdentityAsync(cancellationToken).ConfigureAwait(false)).Epoch;

    internal async Task<(string Epoch, string JournalId)> ReadIdentityAsync(CancellationToken cancellationToken = default)
    {
        var header = await ReadBindingAsync(cancellationToken).ConfigureAwait(false);
        return (header.Epoch, header.JournalId);
    }

    internal async Task<Header> ReadBindingAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _journalLock.EnterAsync(exclusive: false, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        var header = (await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false)).Header;
        return header;
    }

    // The coordinator persists the next epoch before rebuilding Redis. Every
    // asynchronous Redis mutation must compare the epoch captured under its fence.
    internal async Task<bool> AdvanceEpochAsync(string expected, string next, CancellationToken cancellationToken = default)
    {
        ValidateEpoch(expected);
        ValidateEpoch(next);
        if (expected == next) throw new ArgumentException("Recovery must advance to a distinct storage epoch.", nameof(next));
        await using var held = await _journalLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        var current = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        if (current.Header.Epoch != expected) return false;
        await WritePageAsync(file, 0, current.Page, HeaderKind,
            JsonSerializer.SerializeToUtf8Bytes(current.Header with { Epoch = next }, JournalJsonContext.Default.Header),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Persist this intent before asking Redis to admit it. Lost Redis responses
    // therefore leave a bounded, durable conservative reservation for census.
    internal async Task<bool> TryRecordIntentAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
    {
        RedisImportStorageBudget.Validate(reservation);
        var payload = Serialize(new Entry(reservation, LocalImportStorageState.Reserved));
        await using var held = await _journalLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        _ = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        var found = await FindAsync(file, reservation.FileId, cancellationToken).ConfigureAwait(false);
        if (found.Entry is { } existing)
        {
            RequireSameReservation(existing.Reservation, reservation);
            if (existing.State != LocalImportStorageState.Reserved)
                throw new InvalidOperationException("The object has already entered a physical mutation lifetime.");
            return true;
        }
        if (found.Slot < 0) return false;
        await WritePageAsync(file, EntryOffset(found.Slot), found.Page, EntryKind, payload, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal Task<bool> MarkWriteStartedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        => TransitionAsync(reservation, LocalImportStorageState.Writing, cancellationToken);

    internal Task<bool> MarkDeletionStartedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        => TransitionAsync(reservation, LocalImportStorageState.Deleting, cancellationToken);

    // Retirement requires durable deletion/absence of every associated physical
    // object, metadata file and native resource. A timeout is never that proof.
    internal Task<bool> RetireDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
        => TransitionAsync(reservation, LocalImportStorageState.Retired, cancellationToken);

    internal async IAsyncEnumerable<LocalImportStorageJournalEntry> ReadRetainedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var held = await _journalLock.EnterAsync(exclusive: false, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        _ = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        for (var slot = 0; slot < _capacity; slot++)
        {
            var page = await ReadPairAsync(file, EntryOffset(slot), cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            var entry = ReadEntry(page);
            if (entry.State != LocalImportStorageState.Retired)
                yield return new LocalImportStorageJournalEntry(entry.Reservation, entry.State, slot);
        }
    }

    // Only exclusive domain recovery calls this after a complete raw census.
    // The callback positively verifies and synchronizes physical absence. It
    // never infers cleanup from a timeout, expired session or missing Redis key.
    internal async Task RetireAbsentAsync(Func<ImportStorageReservation, bool> verifyDurableAbsence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifyDurableAbsence);
        await using var held = await _journalLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        _ = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        for (var slot = 0; slot < _capacity; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadPairAsync(file, EntryOffset(slot), cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            var entry = ReadEntry(page);
            if (entry.State == LocalImportStorageState.Retired || !verifyDurableAbsence(entry.Reservation)) continue;
            await WritePageAsync(file, EntryOffset(slot), page, EntryKind,
                Serialize(entry with { State = LocalImportStorageState.Retired }), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TransitionAsync(ImportStorageReservation reservation, LocalImportStorageState next,
        CancellationToken cancellationToken)
    {
        RedisImportStorageBudget.Validate(reservation);
        await using var held = await _journalLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        await using var file = Open();
        _ = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
        var found = await FindAsync(file, reservation.FileId, cancellationToken).ConfigureAwait(false);
        if (found.Entry is not { } current) return false;
        RequireSameReservation(current.Reservation, reservation);
        if (current.State == next) return next != LocalImportStorageState.Writing;
        var allowed = next switch
        {
            LocalImportStorageState.Writing => current.State == LocalImportStorageState.Reserved,
            LocalImportStorageState.Deleting => current.State is LocalImportStorageState.Reserved or LocalImportStorageState.Writing,
            LocalImportStorageState.Retired => current.State is LocalImportStorageState.Reserved or LocalImportStorageState.Deleting,
            _ => false
        };
        if (!allowed) return false;
        await WritePageAsync(file, EntryOffset(found.Slot), found.Page, EntryKind,
            Serialize(current with { State = next }), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(int Slot, Page? Page, Entry? Entry)> FindAsync(FileStream file, string fileId, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fileId));
        var start = (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (uint)_capacity);
        (int Slot, Page? Page, Entry? Entry) reusable = (-1, null, null);
        for (var probe = 0; probe < _capacity; probe++)
        {
            var slot = (start + probe) % _capacity;
            var page = await ReadPairAsync(file, EntryOffset(slot), cancellationToken).ConfigureAwait(false);
            if (page is null) return reusable.Slot >= 0 ? reusable : (slot, null, null);
            var entry = ReadEntry(page);
            if (entry.Reservation.FileId == fileId) return (slot, page, entry);
            if (entry.State == LocalImportStorageState.Retired && reusable.Slot < 0) reusable = (slot, page, null);
        }
        return reusable;
    }

    private FileStream Open()
    {
        RejectExistingLinks(_path);
        var file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (file.Length == AllocatedBytes) return file;
        file.Dispose();
        throw new InvalidDataException("The import journal has an unexpected physical size.");
    }

    private async Task<(Header Header, Page Page)> ReadHeaderAsync(FileStream file, CancellationToken cancellationToken)
    {
        var page = await ReadPairAsync(file, 0, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The import journal is not initialized.");
        var header = page.Kind == HeaderKind ? JsonSerializer.Deserialize(page.Payload, JournalJsonContext.Default.Header) : null;
        if (header is null || header.DomainId != _domainId || header.Capacity != _capacity || !IsEpoch(header.Epoch) ||
            !IsEpoch(header.JournalId) || !IsRootFingerprint(header.RootsSha256))
            throw new InvalidDataException("The import journal does not match the configured storage domain.");
        return (header, page);
    }

    private static Entry ReadEntry(Page page)
    {
        var entry = page.Kind == EntryKind ? JsonSerializer.Deserialize(page.Payload, JournalJsonContext.Default.Entry) : null;
        if (entry is null || !Enum.IsDefined(entry.State)) throw new InvalidDataException("Invalid import journal entry.");
        RedisImportStorageBudget.Validate(entry.Reservation);
        return entry;
    }

    private static async Task<Page?> ReadPairAsync(FileStream file, long offset, CancellationToken cancellationToken)
    {
        Page? newest = null;
        var buffer = new byte[PageBytes];
        for (var side = 0; side < 2; side++)
        {
            file.Position = offset + ((long)side * PageBytes);
            await file.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(20));
            if (BinaryPrimitives.ReadUInt64LittleEndian(buffer) != Magic || sequence is 0 or ulong.MaxValue ||
                length is < 1 or > PageBytes - HeaderBytes - HashBytes ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(buffer.AsSpan(0, PageBytes - HashBytes)), buffer.AsSpan(PageBytes - HashBytes)))
                throw new InvalidDataException("The import journal contains an uncertain or corrupt page; recovery is required.");
            if (newest?.Sequence == sequence)
                throw new InvalidDataException("The import journal contains conflicting page generations.");
            if (newest is null || sequence > newest.Sequence)
                newest = new Page(sequence, buffer[16], buffer.AsSpan(HeaderBytes, length).ToArray(), side);
        }
        return newest;
    }

    private static async Task WritePageAsync(FileStream file, long offset, Page? prior, byte kind, byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > PageBytes - HeaderBytes - HashBytes)
            throw new InvalidDataException("The import reservation exceeds its fixed journal allowance.");
        var sequence = checked((prior?.Sequence ?? 0) + 1);
        if (sequence == ulong.MaxValue) throw new InvalidDataException("The import journal generation is exhausted.");
        var page = new byte[PageBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(page, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(8), sequence);
        page[16] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(20), payload.Length);
        payload.CopyTo(page, HeaderBytes);
        SHA256.HashData(page.AsSpan(0, PageBytes - HashBytes), page.AsSpan(PageBytes - HashBytes));
        file.Position = offset + ((long)(prior is null ? 0 : 1 - prior.Side) * PageBytes);
        await file.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    private static byte[] Serialize(Entry entry) => JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonContext.Default.Entry);
    private static long EntryOffset(int slot) => checked((2L + (2L * slot)) * PageBytes);
    private static bool IsEpoch(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out var parsed)
        && parsed.ToString("N") == value;
    private static void ValidateEpoch(string epoch)
    {
        if (!IsEpoch(epoch)) throw new ArgumentException("A storage epoch must be a canonical GUID identity.", nameof(epoch));
    }

    private static void RequireSameReservation(ImportStorageReservation actual, ImportStorageReservation expected)
    {
        if (actual != expected) throw new InvalidOperationException("The journal identity belongs to a different immutable reservation.");
    }

    private static void RejectExistingLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Import journal paths cannot contain symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    [LibraryImport("libc", EntryPoint = "posix_fallocate")]
    private static partial int Allocate(SafeFileHandle file, long offset, long length);

    private sealed record Page(ulong Sequence, byte Kind, byte[] Payload, int Side);
    private static bool IsRootFingerprint(string? value) => value is null ||
        value.Length == 64 && !value.AsSpan().ContainsAnyExcept(HexDigits);

    internal sealed record Header(string DomainId, int Capacity, string Epoch, string JournalId, string? RootsSha256);
    internal sealed record Entry(ImportStorageReservation Reservation, LocalImportStorageState State);
}

internal enum LocalImportStorageState { Reserved, Writing, Deleting, Retired }
internal sealed record LocalImportStorageJournalEntry(ImportStorageReservation Reservation, LocalImportStorageState State, int Slot);

[JsonSerializable(typeof(LocalImportStorageJournal.Header))]
[JsonSerializable(typeof(LocalImportStorageJournal.Entry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
