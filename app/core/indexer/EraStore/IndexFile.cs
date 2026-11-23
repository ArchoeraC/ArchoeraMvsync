// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>提交槽（32B）：gen / manifest_off / manifest_len / manifest_crc / slot_seq / reserved。</summary>
internal sealed class CommitSlot
{
    public ulong Gen;
    public ulong ManifestOffset;
    public uint ManifestLength;
    public uint ManifestCrc;
    public ulong SlotSeq;
}

/// <summary>
/// 单个世代索引文件（Header + 双提交槽 + 只追加数据区 + 尾部 Manifest）。
///
/// 提交顺序不可交换：数据段 → <c>fsync</c> → 追加 Manifest → <c>fsync</c> →
/// 覆盖较旧的槽 → <c>fsync</c>。打开时取"CRC 有效且 <c>slot_seq</c> 最大"的槽；
/// 两槽都无效 → <see cref="HasCommit"/> = false（调用方重建）。
/// 所有公开成员内部加锁；<see cref="Commit"/> 期间不并发。
/// </summary>
internal sealed class IndexFile : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private int _currentSlot = -1;
    private Manifest? _current;
    private ulong _gen;
    private ulong _lastSlotSeq;
    private bool _disposed;

    private IndexFile(FileStream stream) => _stream = stream;

    /// <summary>是否有有效提交；无提交为空文件（仅 Header + 全零槽）。</summary>
    public bool HasCommit { get { lock (_gate) return _current is not null; } }
    /// <summary>当前世代号；无提交为 0。</summary>
    public ulong Gen { get { lock (_gate) return _gen; } }
    /// <summary>当前 Manifest；无提交为 null。</summary>
    public Manifest? Current { get { lock (_gate) return _current; } }
    /// <summary>当前槽下标（0 / 1）；无提交为 -1。</summary>
    public int CurrentSlotIndex { get { lock (_gate) return _currentSlot; } }
    /// <summary>当前文件字节数（含未提交的追尾段）；合并用它计算 <c>file_bytes</c>。</summary>
    public long Length { get { lock (_gate) return _stream.Length; } }

    /// <summary>新建（或覆盖）文件，写入 Header 与两个全零槽。</summary>
    public static IndexFile CreateNew(string path, ReadOnlySpan<byte> fileUuid, ulong createdMs)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (fileUuid.Length != EraFormat.FileUuidBytes)
            throw new ArgumentException($"file_uuid 必须是 {EraFormat.FileUuidBytes} 字节", nameof(fileUuid));

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            stream.Write(BuildHeader(fileUuid, createdMs));
            stream.Write(new byte[EraFormat.CommitSlotSize * EraFormat.CommitSlotCount]);
            stream.Flush(flushToDisk: true);
            return new IndexFile(stream);
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>
    /// 打开可读写文件并恢复最新有效提交。文件过短 / Header 损坏抛 <see cref="InvalidDataException"/>；
    /// 槽无效只跳过，两槽都无效时 <see cref="HasCommit"/> = false。
    /// </summary>
    public static IndexFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var file = new IndexFile(stream);
            file.Recover();
            return file;
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>读取原始槽（0 / 1），供诊断与测试；不做有效性判断。</summary>
    public CommitSlot ReadSlot(int index)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadSlotLocked(index);
        }
    }

    /// <summary>追加一段到文件尾（≥128），返回绝对偏移 / 长度 / CRC / docs。</summary>
    public SectionRef AppendSection(SectionKind kind, ReadOnlySpan<byte> body, uint docs = 0)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var offset = (ulong)_stream.Length;
            if (offset < EraFormat.DataStart)
                throw new InvalidDataException("索引文件短于数据区起点");

            _stream.Position = _stream.Length;
            _stream.Write(body);
            return new SectionRef
            {
                Kind = kind,
                Offset = offset,
                Length = (uint)body.Length,
                Crc = Crc32C.Compute(body),
                Docs = docs,
            };
        }
    }

    /// <summary>
    /// 提交新 Manifest：落盘数据段 → 追加 Manifest → 落盘 → 覆盖较旧槽 → 落盘。
    /// <see cref="Manifest.Gen"/> 必须大于当前 <see cref="Gen"/>。
    /// </summary>
    public void Commit(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (manifest.Gen <= _gen)
                throw new ArgumentException($"新 gen {manifest.Gen} 必须大于当前 {_gen}", nameof(manifest));

            var json = manifest.ToJsonBytes();
            if (json.Length > EraFormat.MaxManifestBytes)
                throw new ArgumentException($"Manifest 超过 {EraFormat.MaxManifestBytes} 字节", nameof(manifest));

            _stream.Flush(flushToDisk: true); // 1) 数据段落盘

            var offset = (ulong)_stream.Length; // 2) Manifest 追加并落盘
            _stream.Position = _stream.Length;
            _stream.Write(json);
            _stream.Flush(flushToDisk: true);

            // 3) 覆盖较旧的槽（首次写槽 0，之后交替），再落盘。
            var target = _currentSlot < 0 ? 0 : 1 - _currentSlot;
            var slot = new CommitSlot
            {
                Gen = manifest.Gen,
                ManifestOffset = offset,
                ManifestLength = (uint)json.Length,
                ManifestCrc = Crc32C.Compute(json),
                SlotSeq = _lastSlotSeq + 1,
            };
            WriteSlotLocked(target, slot);
            _stream.Flush(flushToDisk: true);

            _gen = manifest.Gen;
            _current = manifest;
            _currentSlot = target;
            _lastSlotSeq = slot.SlotSeq;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }

    private void Recover()
    {
        if (_stream.Length < EraFormat.DataStart)
            throw new InvalidDataException("索引文件短于 Header + 双槽");

        Span<byte> header = stackalloc byte[EraFormat.HeaderSize];
        ReadAt(0, header);
        ValidateHeader(header);

        var slots = new CommitSlot?[EraFormat.CommitSlotCount];
        var manifests = new Manifest?[EraFormat.CommitSlotCount];
        var best = -1;

        for (var i = 0; i < EraFormat.CommitSlotCount; i++)
        {
            CommitSlot slot;
            try
            {
                slot = ReadSlotLocked(i);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            var manifest = TryReadManifest(slot);
            if (manifest is null || slot.Gen != manifest.Gen) continue;

            slots[i] = slot;
            manifests[i] = manifest;
            if (slot.SlotSeq > _lastSlotSeq) _lastSlotSeq = slot.SlotSeq;
            if (best < 0 || slot.SlotSeq > slots[best]!.SlotSeq) best = i;
        }

        if (best < 0) return;
        _currentSlot = best;
        _current = manifests[best];
        _gen = slots[best]!.Gen;
    }

    /// <summary>读取并校验槽指向的 Manifest；任何无效只返回 null。</summary>
    private Manifest? TryReadManifest(CommitSlot slot)
    {
        if (slot.Gen < 1) return null;
        if (slot.ManifestLength == 0 || slot.ManifestLength > EraFormat.MaxManifestBytes) return null;
        if (slot.ManifestOffset < EraFormat.DataStart) return null;

        var end = slot.ManifestOffset + slot.ManifestLength;
        if (end < slot.ManifestOffset || end > (ulong)_stream.Length) return null;

        var bytes = new byte[slot.ManifestLength];
        try
        {
            ReadAt((long)slot.ManifestOffset, bytes);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        if (Crc32C.Compute(bytes) != slot.ManifestCrc) return null;

        try
        {
            return Manifest.Parse(bytes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private CommitSlot ReadSlotLocked(int index)
    {
        if ((uint)index >= EraFormat.CommitSlotCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        var at = EraFormat.CommitSlotOffset + index * EraFormat.CommitSlotSize;
        if (at + EraFormat.CommitSlotSize > _stream.Length)
            throw new InvalidDataException("提交槽越界");

        Span<byte> raw = stackalloc byte[EraFormat.CommitSlotSize];
        ReadAt(at, raw);
        return new CommitSlot
        {
            Gen = BinaryPrimitives.ReadUInt64LittleEndian(raw),
            ManifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]),
            ManifestLength = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
            ManifestCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]),
            SlotSeq = BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]),
        };
    }

    private void WriteSlotLocked(int index, CommitSlot slot)
    {
        Span<byte> raw = stackalloc byte[EraFormat.CommitSlotSize];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, slot.Gen);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[8..], slot.ManifestOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[16..], slot.ManifestLength);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[20..], slot.ManifestCrc);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[24..], slot.SlotSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[28..], 0);

        _stream.Position = EraFormat.CommitSlotOffset + index * EraFormat.CommitSlotSize;
        _stream.Write(raw);
    }

    private void ReadAt(long position, Span<byte> destination)
    {
        _stream.Position = position;
        _stream.ReadExactly(destination);
    }

    private static byte[] BuildHeader(ReadOnlySpan<byte> fileUuid, ulong createdMs)
    {
        var w = new ByteWriter(EraFormat.HeaderSize);
        w.WriteBytes(EraFormat.Magic);
        w.WriteUInt16(EraFormat.FormatVersion);
        w.WriteUInt16(0);
        w.WriteUInt16(EraFormat.HeaderSize);
        w.WriteUInt16(0);
        w.WriteBytes(fileUuid);
        w.WriteUInt64(createdMs);
        w.WriteBytes(new byte[20]);
        w.WriteUInt32(Crc32C.Compute(w.Span[..60]));
        return w.ToArray();
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < EraFormat.HeaderSize)
            throw new InvalidDataException("Header 长度不足");
        if (!header[..EraFormat.Magic.Length].SequenceEqual(EraFormat.Magic))
            throw new InvalidDataException("Header magic 不符");

        var format = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (format != EraFormat.FormatVersion)
            throw new InvalidDataException($"Header format {format} 不受支持");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        if (flags != 0)
            throw new InvalidDataException($"Header 含未知必需 flags 0x{flags:X4}");
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        if (headerSize != EraFormat.HeaderSize)
            throw new InvalidDataException($"Header header_size {headerSize} 不符");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[60..]) != Crc32C.Compute(header[..60]))
            throw new InvalidDataException("Header CRC 不符");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// <c>CURRENT</c> 指针（目录内唯一可原子替换的小文本文件），内容为 <c>search-&lt;N&gt;.eraindex\n</c>。
/// 写入用同目录临时文件 + <see cref="File.Move"/> 原子替换。
/// </summary>
internal static class CurrentPointer
{
    /// <summary>指针文件名。</summary>
    public const string FileName = "CURRENT";

    private const string Prefix = "search-";
    private const string Suffix = ".eraindex";

    /// <summary>读取并去空白；缺失 / 非法内容返回 null。</summary>
    public static string? Read(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            var name = File.ReadAllText(path).Trim();
            return IsValidName(name) ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>原子写入（同目录临时文件 + 覆盖替换）。</summary>
    public static void Write(string directory, string fileName)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!IsValidName(fileName))
            throw new ArgumentException("CURRENT 内容必须是 search-<N>.eraindex", nameof(fileName));

        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, FileName);
        var temp = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(fileName + "\n"));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, final, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static bool IsValidName(string name)
    {
        if (name.Length <= Prefix.Length + Suffix.Length) return false;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!name.EndsWith(Suffix, StringComparison.Ordinal)) return false;

        var digits = name.AsSpan(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
        if (digits.Length > 18) return false;
        foreach (var c in digits)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}
