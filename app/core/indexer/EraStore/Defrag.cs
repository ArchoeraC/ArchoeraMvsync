// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;

namespace Archoera.Indexer.EraStore;

/// <summary>去碎片参数。</summary>
internal sealed class DefragOptions
{
    /// <summary>
    /// 切换 <c>CURRENT</c> 后是否删除其它 <c>search-*.eraindex</c>（与
    /// <c>IndexerEngine.CleanupEraFiles</c> 同一约定：只保留新世代）。默认 true。
    /// 关闭只用于测试「切换后、清理前」的中间状态。
    /// </summary>
    public bool Cleanup = true;

    /// <summary>阶段回调；异常向上抛，调用方据此中止（中止时新文件会尽力删除）。</summary>
    public Action<DefragProgress>? Progress = null;
}

/// <summary>去碎片进度：阶段、已复制 / 待复制字节。</summary>
internal readonly record struct DefragProgress(string Phase, long CopiedBytes, long TotalBytes);

/// <summary>去碎片结果。</summary>
internal sealed class DefragResult
{
    /// <summary>是否真的执行（<see cref="Defragmenter.DefragIfGarbage"/> 未达触发线为 false）。</summary>
    public bool Ran;

    /// <summary>提交后的新世代号；未执行为 0。</summary>
    public ulong Gen;

    /// <summary>新世代文件名（<c>search-&lt;N&gt;.eraindex</c>）；未执行为空。</summary>
    public string GenerationName = string.Empty;

    /// <summary>整理前文件字节。</summary>
    public long FileBytesBefore;

    /// <summary>整理后文件字节（Header + 双槽 + 只被引用的段体 + Manifest）。</summary>
    public long FileBytesAfter;

    /// <summary>Manifest 声明的活数据字节。</summary>
    public long LiveBytes;

    /// <summary>整理前 <c>fileBytes - DataStart - liveBytes</c>（含旧 Manifest / 旧段垃圾）。</summary>
    public long OverheadBytesBefore;

    /// <summary>整理后 <c>fileBytes - DataStart - liveBytes</c>（只剩 Manifest）。</summary>
    public long OverheadBytesAfter;

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs;
}

/// <summary>
/// EraStore 去碎片 / 世代重写（A2）。
///
/// <para>为什么需要：合并（<see cref="Merger.Merge"/>）把新段**追加**到同一世代文件，
/// 旧段与旧 Manifest 留在原地成为垃圾。全量构建的自动压缩因此会让文件长期停在
/// ≈2× 活数据；<c>stats</c> 的 <c>fileBytes</c> / 设置页显示的都是这个偏大的值。
/// 本类把「当前世代」重写成一个只含被引用段体的新世代文件。</para>
///
/// <para>布局：Header + 双提交槽 + 按 Manifest 顺序逐段原样复制的段体 + 新 Manifest。
/// **不重新编码、不重新分析**：段体字节逐字复制（只重算 CRC 并与旧值比对），
/// 因此词条 / 倒排 / 位置 / 文档列与旧世代逐字节一致，查询结果（track id + score）不变。
/// Manifest 除 <c>gen</c> / <c>sections</c> / <c>stats.liveBytes</c> / <c>stats.fileBytes</c>
/// 外全部原样保留（metaVersion、optionsBits、capabilities、anchors、stats.docs 等）。</para>
///
/// <para>崩溃安全：新世代先写进**另一个**文件并提交；只有新文件完全落盘后，
/// 才用 <see cref="CurrentPointer.Write"/> 原子切换。切换前任何失败 / 中止 / 崩溃都
/// 不会改动旧文件或 <c>CURRENT</c>，旧世代继续可读；切换后清理旧文件失败也只是
/// 留下垃圾（下次维护按同一约定删除）。</para>
/// </summary>
internal static class Defragmenter
{
    /// <summary>
    /// 自动触发的最小绝对垃圾量（**已扣掉 Manifest**）。纯净索引的可见垃圾为 0；
    /// 加绝对下限避免「为几百字节 / 几十 KB 的整理而重写整个文件」。
    /// </summary>
    public const long MinAutoGarbageBytes = 64L * 1024;

    /// <summary>
    /// 把当前世代重写成只含活段体的新世代并原子切换 <c>CURRENT</c>。
    /// <paramref name="indexPath"/> 可为目录（取 CURRENT）或当前世代文件；文件必须与
    /// 同目录 <c>CURRENT</c> 指向一致（防止把旧世代误设为当前）。
    /// </summary>
    public static DefragResult Defrag(string indexPath, DefragOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        var opt = options ?? new DefragOptions();

        var resolved = IndexPaths.Resolve(indexPath);
        var full = Path.GetFullPath(resolved);
        var dir = Path.GetDirectoryName(full)
            ?? throw new InvalidDataException($"无法确定索引目录: {indexPath}");
        var fileName = Path.GetFileName(full);

        var current = CurrentPointer.Read(dir);
        if (current is not null && !string.Equals(current, fileName, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"defrag 只能作用于当前世代：CURRENT={current}，给定={fileName}");

        var fileBytesBefore = new FileInfo(full).Length;
        var (fileUuid, createdMs) = IndexFile.ReadIdentity(full);

        var newName = CurrentPointer.NextGenerationName(dir);
        var newPath = Path.Combine(dir, newName);
        var sections = new List<SectionRef>();
        var liveBytes = 0L;
        var copied = 0L;
        var result = new DefragResult { FileBytesBefore = fileBytesBefore };

        var watch = Stopwatch.StartNew();
        IndexFile? target = null;
        var switched = false;
        try
        {
            target = IndexFile.CreateNew(newPath, fileUuid, createdMs);

            Manifest old;
            // 复制阶段结束后立即释放旧文件的 mmap / 句柄：Windows 上删除被打开的文件会失败，
            // 清理旧世代必须在 reader 关闭之后（当前世代此时已完全写进新文件）。
            using (var reader = new StoreReader(full))
            {
                old = reader.Manifest;
                foreach (var section in old.Sections)
                {
                    var body = reader.GetSectionBody(section);
                    var appended = target.AppendSection(section.Kind, body.Span, section.Docs);
                    if (appended.Crc != section.Crc)
                        throw new InvalidDataException(
                            $"seg={section.SegmentId} {section.Kind} 段体 CRC 不符"
                            + $"（0x{appended.Crc:X8} != 0x{section.Crc:X8}），拒绝复制损坏数据");
                    sections.Add(new SectionRef
                    {
                        Kind = section.Kind,
                        SegmentId = section.SegmentId,
                        Offset = appended.Offset,
                        Length = appended.Length,
                        Crc = appended.Crc,
                        Docs = section.Docs,
                    });
                    liveBytes += section.Length;
                    copied += section.Length;
                    opt.Progress?.Invoke(new DefragProgress("copy", copied, old.LiveBytes));
                }
            }

            result.LiveBytes = old.LiveBytes;
            result.OverheadBytesBefore = Math.Max(
                0, fileBytesBefore - EraFormat.DataStart - old.LiveBytes);
            var newGen = checked(old.Gen + 1);

            var manifest = new Manifest
            {
                Gen = newGen,
                Format = old.Format,
                MetaVersion = old.MetaVersion,
                // 只搬物理数据：查询元数据全部原样保留（同 Merger.BuildManifest 的约定）。
                Anchors = old.Anchors,
                OptionsBits = old.OptionsBits,
                Capabilities = new List<string>(old.Capabilities),
                DocCount = old.DocCount,
                LiveBytes = liveBytes,
                Sections = sections,
            };
            IndexBuilder.FillFileBytes(manifest, target.Length);

            opt.Progress?.Invoke(new DefragProgress("commit", copied, old.LiveBytes));
            target.Commit(manifest);

            // 唯一的外部可见切换点：原子替换 CURRENT。此前旧世代始终是「当前」。
            opt.Progress?.Invoke(new DefragProgress("switch", copied, old.LiveBytes));
            CurrentPointer.Write(dir, newName);
            switched = true;

            target.Dispose();
            target = null;

            result.Ran = true;
            result.Gen = newGen;
            result.GenerationName = newName;
            result.FileBytesAfter = new FileInfo(newPath).Length;
            result.OverheadBytesAfter = Math.Max(
                0, result.FileBytesAfter - EraFormat.DataStart - liveBytes);

            if (opt.Cleanup) CleanupGenerations(dir, newName);
        }
        catch
        {
            // 切换前失败：新文件从未被引用，尽力删除；旧世代与 CURRENT 未动。
            // 切换后失败：新文件已经是当前世代，**绝不能删除**（否则 CURRENT 指向空文件）；
            // 旧文件留给下一次维护按同一清理约定回收。
            target?.Dispose();
            if (!switched) TryDelete(newPath);
            throw;
        }
        finally
        {
            watch.Stop();
            result.ElapsedMs = watch.ElapsedMilliseconds;
        }

        return result;
    }

    /// <summary>
    /// 只在存在**可见垃圾**时整理：先扣掉 Manifest 自身占用的字节，再要求
    /// <c>garbage &gt; minOverheadRatio × liveBytes</c> 且超过 <paramref name="minGarbageBytes"/>。
    /// 纯净索引的 <c>overheadBytes</c> 就是 Manifest（几百字节到几 KB），garbage 为 0，
    /// 因此即使 <paramref name="minGarbageBytes"/> 传 0 也不会被整理。
    /// </summary>
    public static DefragResult DefragIfGarbage(
        string indexPath, double minOverheadRatio, long minGarbageBytes = MinAutoGarbageBytes,
        DefragOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        var resolved = IndexPaths.Resolve(indexPath);
        var manifest = StoreReader.TryReadManifest(resolved)
            ?? throw new InvalidDataException("索引没有有效提交");
        var fileBytes = new FileInfo(resolved).Length;
        var overhead = Math.Max(0, fileBytes - EraFormat.DataStart - manifest.LiveBytes);
        var garbage = Math.Max(0, overhead - manifest.ToJsonBytes().Length);
        var threshold = Math.Max(
            minGarbageBytes,
            (long)Math.Ceiling(Math.Max(0.0, minOverheadRatio) * manifest.LiveBytes));

        if (garbage <= threshold)
        {
            return new DefragResult
            {
                FileBytesBefore = fileBytes,
                FileBytesAfter = fileBytes,
                LiveBytes = manifest.LiveBytes,
                OverheadBytesBefore = overhead,
                OverheadBytesAfter = overhead,
            };
        }
        return Defrag(resolved, options);
    }

    /* ------------------------------------------------------------------ */

    /// <summary>与 <c>IndexerEngine.CleanupEraFiles</c> 相同的清理约定：只保留新世代。</summary>
    private static void CleanupGenerations(string directory, string keep) =>
        CurrentPointer.CleanupGenerations(directory, keep);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
