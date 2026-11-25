// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text.Json;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// <c>verify</c> 报告（设计文档 §3.10 / §5.3 P8）。
/// 结构 / CRC 问题一律进入 <see cref="Errors"/>，不中断扫描；<see cref="Ok"/> 表示零错误。
/// </summary>
internal sealed class VerifyReport
{
    /// <summary>是否全部检查通过。</summary>
    public bool Ok;

    /// <summary>当前世代号。</summary>
    public ulong Gen;

    /// <summary>段数（Manifest 中不同段号的数量）。</summary>
    public int Segments;

    /// <summary>段引用总数。</summary>
    public int Sections;

    /// <summary>Manifest 声明的文档数。</summary>
    public uint Docs;

    /// <summary>索引 Manifest 记录的数据包 id（<c>anchors.dataPackId</c>；旧索引为空）。</summary>
    public string DataPackId = "";

    /// <summary><c>--data</c>：磁盘数据包的全量校验结果（未请求时为 null，键不输出）。</summary>
    public DataPackVerification? DataPack;

    /// <summary>人类可读错误（带段号 / 段类型）。</summary>
    public List<string> Errors = new();

    /// <summary>序列化为单行 UTF-8 JSON。</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", Ok);
            w.WriteNumber("gen", Gen);
            w.WriteNumber("segments", Segments);
            w.WriteNumber("sections", Sections);
            w.WriteNumber("docs", Docs);
            w.WriteString("dataPackId", DataPackId);
            if (DataPack is not null)
            {
                w.WritePropertyName("dataPack");
                w.WriteStartObject();
                w.WriteBoolean("ok", DataPack.Ok);
                w.WriteString("id", DataPack.Id);
                w.WriteString("embeddedId", DataPack.EmbeddedId);
                w.WriteBoolean("match", DataPack.Match);
                w.WriteBoolean("present", DataPack.Present);
                w.WriteStartArray("missing");
                foreach (var name in DataPack.Missing) w.WriteStringValue(name);
                w.WriteEndArray();
                w.WriteStartArray("corrupt");
                foreach (var name in DataPack.Corrupt) w.WriteStringValue(name);
                w.WriteEndArray();
                w.WriteStartArray("degradedCapabilities");
                foreach (var capability in DataPack.Degraded) w.WriteStringValue(capability);
                w.WriteEndArray();
                w.WriteStartArray("errors");
                foreach (var error in DataPack.Errors) w.WriteStringValue(error);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteStartArray("errors");
            foreach (var error in Errors) w.WriteStringValue(error);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>
/// 诊断命令的路径解析：目录 → <c>CURRENT</c> 指向的世代文件；文件 → 原样使用。
/// 目录缺少 / 非法 <c>CURRENT</c> 抛 <see cref="InvalidDataException"/>，由 CLI 转成 JSON 错误。
/// </summary>
internal static class IndexPaths
{
    /// <summary>解析为实际的 <c>.eraindex</c> 文件路径。</summary>
    public static string Resolve(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
            throw new InvalidDataException("索引路径为空");

        if (Directory.Exists(indexPath))
        {
            var name = CurrentPointer.Read(indexPath)
                ?? throw new InvalidDataException($"目录 {indexPath} 缺少有效的 CURRENT");
            if (name.Contains('/') || name.Contains('\\')
                || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
                throw new InvalidDataException($"CURRENT 内容含路径分隔符: {name}");
            return Path.Combine(indexPath, name);
        }

        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"索引文件不存在: {indexPath}", indexPath);
        return indexPath;
    }
}

/// <summary>
/// 全量只读校验（<c>verify</c>）：Header / 双槽 / Manifest / 段体 CRC / 段类型语义。
/// 损坏只记录到 <see cref="VerifyReport.Errors"/>；文件不可读等外因照常抛出。
/// </summary>
internal static class IndexVerifier
{
    /// <summary>
    /// 接受目录或 <c>.eraindex</c> 文件路径。
    ///
    /// <paramref name="dataPack"/> 非空时把磁盘数据包的全量校验结果一并写入报告
    /// （<c>verify --data</c>）；普通 <c>verify</c> 只回显索引记录的 <c>dataPackId</c>。
    /// </summary>
    public static VerifyReport Verify(string indexPath, DataPackVerification? dataPack = null)
    {
        var path = IndexPaths.Resolve(indexPath);
        var raw = File.ReadAllBytes(path);
        var report = new VerifyReport { DataPack = dataPack };

        IndexFile file;
        try
        {
            file = IndexFile.Open(path);
        }
        catch (InvalidDataException e)
        {
            report.Errors.Add("header/commit: " + e.Message);
            report.Ok = false;
            return report;
        }

        using (file)
        {
            report.Gen = file.Gen;
            if (!file.HasCommit || file.Current is null)
            {
                report.Errors.Add("commit: 两个提交槽都没有有效提交");
                report.Ok = false;
                return report;
            }

            var manifest = file.Current;
            report.Docs = manifest.DocCount;
            report.DataPackId = manifest.Anchors.DataPackId;
            report.Sections = manifest.Sections.Count;
            report.Segments = manifest.Sections.Select(s => (int)s.SegmentId).Distinct().Count();

            var manifestStart = CurrentManifestOffset(file, raw.Length);
            VerifySections(raw, manifest, manifestStart, report);
            report.Ok = report.Errors.Count == 0 && (dataPack is null || dataPack.Ok);
            return report;
        }
    }

    /// <summary>
    /// 只校验磁盘数据包（<c>verify --data --data-dir …</c>，不要求索引存在）：
    /// 逐文件 sha256 + TSV 结构 + 与索引器内嵌数据包的 id 一致性。
    /// </summary>
    public static VerifyReport VerifyDataPack(DataPack pack)
    {
        var verification = pack.VerifyAll(validateTables: true);
        var report = new VerifyReport
        {
            DataPack = verification,
            DataPackId = pack.Id,
            Ok = verification.Ok,
        };
        report.Errors.AddRange(verification.Errors);
        return report;
    }

    /// <summary>
    /// 当前槽指向的 Manifest 起始偏移；取不到返回 null（跳过覆盖检查）。
    /// 旧 Manifest 与旧段一样是只追加文件里的垃圾（§3.8.2.3），不能用来限制新段的偏移。
    /// </summary>
    private static long? CurrentManifestOffset(IndexFile file, int fileLength)
    {
        if (file.CurrentSlotIndex < 0) return null;

        CommitSlot slot;
        try
        {
            slot = file.ReadSlot(file.CurrentSlotIndex);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (slot.ManifestOffset < EraFormat.DataStart
            || slot.ManifestOffset > (ulong)fileLength)
            return null;
        return (long)slot.ManifestOffset;
    }

    private static void VerifySections(
        byte[] raw, Manifest manifest, long? manifestStart, VerifyReport report)
    {
        var kinds = new Dictionary<(ushort, SectionKind), SectionRef>();
        var untrusted = new HashSet<(ushort, SectionKind)>();
        var usable = new List<SectionRef>();

        foreach (var section in manifest.Sections)
        {
            var key = (section.SegmentId, section.Kind);
            if (!kinds.TryAdd(key, section))
            {
                report.Errors.Add($"seg={section.SegmentId} {section.Kind} 段重复");
                untrusted.Add(key);
                continue;
            }

            if (!CheckBounds(raw, section, manifestStart, report))
            {
                untrusted.Add(key);
                continue;
            }

            usable.Add(section);
            var actual = Crc32C.Compute(raw.AsSpan((int)section.Offset, (int)section.Length));
            if (actual != section.Crc)
            {
                report.Errors.Add(
                    $"seg={section.SegmentId} {section.Kind} 段体 crc 不符"
                    + $"（0x{actual:X8} != 0x{section.Crc:X8}）");
                untrusted.Add(key);
            }
        }

        CheckOverlap(usable, report);
        VerifySegments(raw, kinds, untrusted, manifest, report);
    }

    /// <summary>偏移 / 长度 / 覆盖 Manifest 可行性；不可用返回 false。</summary>
    private static bool CheckBounds(
        byte[] raw, SectionRef section, long? manifestStart, VerifyReport report)
    {
        var fileLength = (ulong)raw.Length;
        if (section.Offset < EraFormat.DataStart)
        {
            report.Errors.Add(
                $"seg={section.SegmentId} {section.Kind} 段偏移 {section.Offset} 小于数据区起点");
            return false;
        }
        if (section.Offset > fileLength || section.Length > fileLength - section.Offset)
        {
            report.Errors.Add(
                $"seg={section.SegmentId} {section.Kind} 段越界"
                + $"（off={section.Offset}, len={section.Length}, file={raw.Length}）");
            return false;
        }
        if (manifestStart is long start && section.Offset + section.Length > (ulong)start)
        {
            report.Errors.Add(
                $"seg={section.SegmentId} {section.Kind} 段体覆盖 Manifest 起始位置（{start}）");
            return false;
        }
        return true;
    }

    private static void CheckOverlap(List<SectionRef> usable, VerifyReport report)
    {
        var ordered = usable.OrderBy(s => s.Offset).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var current = ordered[i];
            if (current.Offset < prev.Offset + prev.Length)
            {
                report.Errors.Add(
                    $"seg={current.SegmentId} {current.Kind} 段体与"
                    + $" seg={prev.SegmentId} {prev.Kind} 段体重叠（off={current.Offset}）");
            }
        }
    }

    private static void VerifySegments(
        byte[] raw,
        Dictionary<(ushort, SectionKind), SectionRef> kinds,
        HashSet<(ushort, SectionKind)> untrusted,
        Manifest manifest,
        VerifyReport report)
    {
        var segmentIds = kinds.Keys.Select(k => k.Item1).Distinct().OrderBy(x => x).ToArray();
        if (segmentIds.Length == 0)
        {
            if (manifest.DocCount > 0)
                report.Errors.Add("缺少 DocStore 段（Manifest 声明了文档）");
            return;
        }

        // 合并会把旧段变成垃圾并向文件尾追加更大段号（§3.8.2.3），
        // 因此 Manifest 里的段号出现空洞是正常状态，不再要求从 0 连续。
        foreach (var segment in segmentIds)
        {
            if (!kinds.ContainsKey((segment, SectionKind.DocStore)))
                report.Errors.Add($"seg={segment} 缺少 DocStore 段");

            if (kinds.TryGetValue((segment, SectionKind.DocStore), out var docStore)
                && !untrusted.Contains((segment, SectionKind.DocStore)))
            {
                try
                {
                    var reader = new DocStoreReader(Body(raw, docStore));
                    if (reader.Count != docStore.Docs)
                        report.Errors.Add(
                            $"seg={segment} DocStore 文档数 {reader.Count} != 声明 {docStore.Docs}");
                }
                catch (InvalidDataException e)
                {
                    report.Errors.Add($"seg={segment} DocStore 解码失败: {e.Message}");
                }
            }

            VerifyDictionary(raw, kinds, untrusted, segment, report);
            VerifyTombs(raw, kinds, untrusted, segment, report);
        }
    }

    /// <summary>校验墓碑段：段 CRC 已通过，这里要求每个 doc_id &lt; 同段 DocStore 文档数。</summary>
    private static void VerifyTombs(
        byte[] raw,
        Dictionary<(ushort, SectionKind), SectionRef> kinds,
        HashSet<(ushort, SectionKind)> untrusted,
        ushort segment,
        VerifyReport report)
    {
        if (!kinds.TryGetValue((segment, SectionKind.Tombs), out var tombs)) return;
        if (untrusted.Contains((segment, SectionKind.Tombs))) return;
        if (!kinds.TryGetValue((segment, SectionKind.DocStore), out var docStore)
            || untrusted.Contains((segment, SectionKind.DocStore)))
            return;

        int count;
        try
        {
            count = new DocStoreReader(Body(raw, docStore)).Count;
        }
        catch (InvalidDataException)
        {
            return; // DocStore 自身损坏已在上面报告。
        }

        try
        {
            Tombstones.Read(Body(raw, tombs), count);
        }
        catch (InvalidDataException e)
        {
            report.Errors.Add($"seg={segment} Tombs 解码失败: {e.Message}");
        }
    }

    private static void VerifyDictionary(
        byte[] raw,
        Dictionary<(ushort, SectionKind), SectionRef> kinds,
        HashSet<(ushort, SectionKind)> untrusted,
        ushort segment,
        VerifyReport report)
    {
        if (!kinds.TryGetValue((segment, SectionKind.Dict), out var dict)) return;

        var hasIndex = kinds.TryGetValue((segment, SectionKind.DictIndex), out var index);
        var hasPostings = kinds.TryGetValue((segment, SectionKind.Postings), out var postings);
        if (dict.Length > 0 && !hasIndex)
            report.Errors.Add($"seg={segment} 有 Dict 但缺少 DictIndex 段");
        if (!hasPostings)
            report.Errors.Add($"seg={segment} 有 Dict 但缺少 Postings 段");

        // 空 Dict 允许缺块索引；也无需解码。
        if (dict.Length == 0) return;
        if (!hasIndex || !hasPostings) return;
        if (untrusted.Contains((segment, SectionKind.Dict))
            || untrusted.Contains((segment, SectionKind.DictIndex))
            || untrusted.Contains((segment, SectionKind.Postings)))
            return;

        List<TermInfo> terms;
        try
        {
            terms = new TermDictReader(Body(raw, dict), Body(raw, index!)).ReadAll();
        }
        catch (InvalidDataException e)
        {
            report.Errors.Add($"seg={segment} 词典解码失败: {e.Message}");
            return;
        }

        var positions = kinds.TryGetValue((segment, SectionKind.Positions), out var pos)
            && !untrusted.Contains((segment, SectionKind.Positions))
                ? Body(raw, pos)
                : ReadOnlyMemory<byte>.Empty;
        var reader2 = new PostingsReader(Body(raw, postings!), positions);

        foreach (var term in terms)
        {
            try
            {
                var entries = reader2.DecodeAll(term.PostOffset, term.Df);
                foreach (var entry in entries)
                {
                    if (entry.Positions is not null && entry.Positions.Length != entry.Tf)
                    {
                        report.Errors.Add(
                            $"seg={segment} 词条 '{term.Term}' 的 tf 与位置数不一致（doc={entry.Doc}）");
                        return;
                    }
                }
            }
            catch (InvalidDataException e)
            {
                report.Errors.Add($"seg={segment} 词条 '{term.Term}' 倒排解码失败: {e.Message}");
                return;
            }
        }
    }

    private static ReadOnlyMemory<byte> Body(byte[] raw, SectionRef section) =>
        raw.AsMemory((int)section.Offset, (int)section.Length);
}

/// <summary><c>stats</c> 报告：只读 Header / 槽 / Manifest，不扫段体。</summary>
internal sealed class StatsReport
{
    /// <summary>当前世代号。</summary>
    public ulong Gen;

    /// <summary>Manifest 声明的文档数（活 doc）。</summary>
    public uint Docs;

    /// <summary>墓碑段声明的死行总数。</summary>
    public int DeadDocs;

    /// <summary>死行比例 <c>dead / (live + dead)</c>；无文档为 0。</summary>
    public double DeadRatio;

    /// <summary>段数。</summary>
    public int Segments;

    /// <summary>活数据字节（Manifest 口径）。</summary>
    public long LiveBytes;

    /// <summary>文件总字节。</summary>
    public long FileBytes;

    /// <summary>Header + 提交槽 + Manifest 等非活数据字节（不小于 0）。</summary>
    public long OverheadBytes;

    /// <summary>各段体长度，按 (段号, 段类型) 排序。</summary>
    public List<(int Segment, SectionKind Kind, uint Bytes)> Sections = new();

    /// <summary>序列化为单行 UTF-8 JSON。</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("gen", Gen);
            w.WriteNumber("docs", Docs);
            w.WriteNumber("deadDocs", DeadDocs);
            w.WriteNumber("deadRatio", DeadRatio);
            w.WriteNumber("segments", Segments);
            w.WriteNumber("liveBytes", LiveBytes);
            w.WriteNumber("fileBytes", FileBytes);
            w.WriteNumber("overheadBytes", OverheadBytes);
            w.WriteStartArray("sections");
            foreach (var (segment, kind, bytes) in Sections)
            {
                w.WriteStartObject();
                w.WriteNumber("seg", segment);
                w.WriteNumber("kind", (byte)kind);
                w.WriteNumber("bytes", bytes);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>索引统计（<c>stats</c> 子命令）。</summary>
internal static class IndexStats
{
    /// <summary>接受目录或 <c>.eraindex</c> 文件路径。</summary>
    public static StatsReport Collect(string indexPath)
    {
        var path = IndexPaths.Resolve(indexPath);
        using var file = IndexFile.Open(path);
        if (!file.HasCommit || file.Current is null)
            throw new InvalidDataException("索引没有有效提交");

        var manifest = file.Current;
        var fileBytes = new FileInfo(path).Length;
        var overhead = fileBytes - manifest.LiveBytes - EraFormat.DataStart;

        // 墓碑段的 Docs = dead_count；DocStore 段 Docs = 物理文档数（含死行）。
        long physicalDocs = 0;
        long deadDocs = 0;
        foreach (var section in manifest.Sections)
        {
            if (section.Kind == SectionKind.DocStore) physicalDocs += section.Docs;
            else if (section.Kind == SectionKind.Tombs) deadDocs += section.Docs;
        }

        var report = new StatsReport
        {
            Gen = file.Gen,
            Docs = manifest.DocCount,
            DeadDocs = (int)Math.Min(deadDocs, int.MaxValue),
            DeadRatio = physicalDocs <= 0 ? 0 : Math.Min(1.0, (double)deadDocs / physicalDocs),
            Segments = manifest.Sections.Select(s => (int)s.SegmentId).Distinct().Count(),
            LiveBytes = manifest.LiveBytes,
            FileBytes = fileBytes,
            OverheadBytes = overhead < 0 ? 0 : overhead,
        };

        foreach (var section in manifest.Sections
                     .OrderBy(s => s.SegmentId)
                     .ThenBy(s => (int)s.Kind))
            report.Sections.Add((section.SegmentId, section.Kind, section.Length));
        return report;
    }
}

/// <summary>一条 dump 词条。</summary>
internal readonly record struct DumpTerm(
    ushort Segment, FieldId Field, string Term, uint Df, uint PostOffset, byte Flags);

/// <summary><c>dump</c> 报告：按 (段号, 键字节序) 输出词条。</summary>
internal sealed class DumpReport
{
    /// <summary>当前世代号。</summary>
    public ulong Gen;

    /// <summary>词条列表。</summary>
    public List<DumpTerm> Terms = new();

    /// <summary>序列化为单行 UTF-8 JSON。</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("gen", Gen);
            w.WriteStartArray("terms");
            foreach (var term in Terms)
            {
                w.WriteStartObject();
                w.WriteNumber("seg", term.Segment);
                w.WriteNumber("field", (byte)term.Field);
                w.WriteString("term", term.Term);
                w.WriteNumber("df", term.Df);
                w.WriteNumber("postOff", term.PostOffset);
                w.WriteNumber("flags", term.Flags);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>词典词条导出（<c>dump</c> 子命令）。</summary>
internal static class IndexDumper
{
    /// <summary>默认词条上限。</summary>
    public const int DefaultLimit = 1000;

    /// <summary>词条上限最大值。</summary>
    public const int MaxLimit = 100_000;

    /// <summary>
    /// 遍历所有段的 <c>Dict</c>；<paramref name="field"/> 按字段族过滤，
    /// <paramref name="prefix"/> 按词条文本前缀过滤，<paramref name="limit"/> 截断。
    /// </summary>
    public static DumpReport Dump(string indexPath, FieldId? field, string? prefix, int limit)
    {
        var path = IndexPaths.Resolve(indexPath);
        var max = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        var raw = File.ReadAllBytes(path);
        var report = new DumpReport();
        List<SectionRef> sections;

        using (var file = IndexFile.Open(path))
        {
            if (!file.HasCommit || file.Current is null)
                throw new InvalidDataException("索引没有有效提交");
            report.Gen = file.Gen;
            sections = file.Current.Sections;
        }

        var dicts = sections
            .Where(s => s.Kind == SectionKind.Dict)
            .GroupBy(s => s.SegmentId)
            .OrderBy(g => g.Key);

        foreach (var group in dicts)
        {
            var dict = group.First();
            var index = sections.FirstOrDefault(
                s => s.SegmentId == group.Key && s.Kind == SectionKind.DictIndex)
                ?? throw new InvalidDataException($"seg={group.Key} 有 Dict 但缺少 DictIndex 段");

            var terms = new TermDictReader(
                raw.AsMemory((int)dict.Offset, (int)dict.Length),
                raw.AsMemory((int)index.Offset, (int)index.Length)).ReadAll();

            foreach (var term in terms)
            {
                if (field is { } wanted && term.Field != wanted) continue;
                if (!string.IsNullOrEmpty(prefix)
                    && !term.Term.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                report.Terms.Add(new DumpTerm(
                    group.Key, term.Field, term.Term, term.Df, term.PostOffset, term.Flags));
                if (report.Terms.Count >= max) return report;
            }
        }
        return report;
    }
}
