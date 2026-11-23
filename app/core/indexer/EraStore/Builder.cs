// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>
/// 已分词的一条文档（P6 只吃 token 序列，normalizer / tokenizer 不在本 PR）。
/// <see cref="Tokens"/> 里每个字段是<b>有序</b> token 数组，数组下标即位置（P5 位置语义）。
/// </summary>
internal readonly record struct AnalyzedDoc(
    byte[] Track,
    byte[] Rev,
    ushort ScriptBits,
    byte Flags,
    IReadOnlyDictionary<FieldId, string[]> Tokens,
    string Title,
    string Artist,
    string Album);

/// <summary>
/// 全量构建参数。<see cref="FileUuid"/> / <see cref="CreatedMs"/> / <see cref="Gen"/> 固定时，
/// 相同输入序列 + 相同分段大小必得逐字节一致的文件（设计文档 §5.3 P6）。
/// </summary>
internal sealed class BuildOptions
{
    /// <summary>每段 doc 数（1..1,000,000）；也是流式构建的驻留上限。</summary>
    public int DocsPerSegment = 16_000;

    /// <summary>
    /// 段内倒排累加器的<b>估算内存</b>上限（不是磁盘段大小），默认 32 MB。
    /// 与 <see cref="DocsPerSegment"/> 先到先得：达到任一上限即落一个段，
    /// 避免 12 字段族（歌词三层 + 拼音全开）把单段驻留量推到百 MB 以上。
    /// </summary>
    public long MaxSegmentBytes = 32L << 20;

    /// <summary>世代号（必须大于 0）。</summary>
    public ulong Gen = 1;

    /// <summary>文件 UUID（16B）；默认随机，测试可注入固定值。</summary>
    public byte[] FileUuid = RandomNumberGenerator.GetBytes(EraFormat.FileUuidBytes);

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public ulong CreatedMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// 不写位置块的字段族（V2：派生转写层只做文档级召回，短语由原文 / 元数据层承担）。
    /// 命中集合与 tf 不受影响；<c>Positions()</c> 返回空，PhraseIter 在该字段降级为
    /// 纯交集（<c>Positioned=false</c>），调用方需在 explain 中标注。
    /// 默认为空 = 全部字段带位置（与历史字节完全一致）。
    /// </summary>
    public HashSet<FieldId> NoPositionFields = new();

    /// <summary>normalizer 版本锚。</summary>
    public int NormalizerVersion = 1;

    /// <summary>tokenizer 版本锚。</summary>
    public int TokenizerVersion = 2;

    /// <summary>进度回调（分析 / 写段）；异常向上抛，由调用方实现取消。</summary>
    public Action<BuildProgress>? Progress = null;
}

/// <summary>构建进度：<c>phase</c>（analyze / write）、已完成 doc 总数、当前段号。</summary>
internal readonly record struct BuildProgress(string Phase, int DocsDone, int SegmentId);

/// <summary>全量构建结果。</summary>
internal sealed class BuildResult
{
    /// <summary>索引文件路径。</summary>
    public string Path = string.Empty;

    /// <summary>已提交的 Manifest。</summary>
    public Manifest Manifest = new();

    /// <summary>段数（空输入为 0）。</summary>
    public int SegmentCount;
}

/// <summary>
/// 全量构建（设计文档 §3.8.2.1 / §3.3.0.1）。
///
/// 单写者流式写段：每段只驻留该段 DocStore 记录与 <c>(field, term) → postings</c> 累加器，
/// 写完立即释放；段内写出顺序固定 DocStore → Dict → DictIndex → Postings →（有位置）Positions。
/// 所有段写完后一次性提交 Manifest（全量构建只在收尾提交一次）。
/// </summary>
internal static class IndexBuilder
{
    private const string PhaseAnalyze = "analyze";
    private const string PhaseWrite = "write";

    /// <summary>词条 flags：该词条带位置块。</summary>
    private const byte DictFlagHasPositions = 0x01;

    /// <summary>进度回调粒度（doc 数）。</summary>
    private const int ProgressStride = 50;

    /// <summary>
    /// 把 <paramref name="docs"/> 写成单个世代索引文件，并原子替换目录内 <c>CURRENT</c>。
    /// 每个段分配连续的段号（0 起），段内 doc_id 从 0 起按输入顺序递增。
    /// </summary>
    public static BuildResult Build(string path, IEnumerable<AnalyzedDoc> docs, BuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(docs);
        var opt = options ?? new BuildOptions();
        if (opt.DocsPerSegment < 1 || opt.DocsPerSegment > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "DocsPerSegment 必须在 1..1000000");
        if (opt.MaxSegmentBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSegmentBytes 必须大于 0");
        if (opt.FileUuid is null || opt.FileUuid.Length != EraFormat.FileUuidBytes)
            throw new ArgumentException($"FileUuid 必须是 {EraFormat.FileUuidBytes} 字节", nameof(options));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var fileName = Path.GetFileName(path);

        using var file = IndexFile.CreateNew(path, opt.FileUuid, opt.CreatedMs);

        var sections = new List<SectionRef>();
        var segmentDocs = new List<DocRecord>(Math.Min(opt.DocsPerSegment, 4096));
        var accumulator = new SegmentAccumulator();
        var segmentId = 0;
        ulong total = 0;
        long liveBytes = 0;

        foreach (var doc in docs)
        {
            if (total >= EraFormat.MaxDocCount)
                throw new InvalidOperationException($"doc 数超过 {EraFormat.MaxDocCount}");
            segmentDocs.Add(MakeRecord(doc));
            accumulator.Add(segmentDocs.Count - 1, doc.Tokens, opt.NoPositionFields);
            total++;

            if (total % ProgressStride == 0)
                Report(opt, PhaseAnalyze, (int)total, segmentId);
            if (segmentDocs.Count >= opt.DocsPerSegment || accumulator.EstimatedBytes >= opt.MaxSegmentBytes)
                Flush();
        }
        Flush();

        void Flush()
        {
            if (segmentDocs.Count == 0) return;
            if (segmentId > ushort.MaxValue)
                throw new InvalidOperationException("段号超过 ushort 上限");
            Report(opt, PhaseWrite, (int)total, segmentId);
            liveBytes += WriteSegment(
                file, segmentDocs, accumulator.SortedTerms(), (ushort)segmentId, sections);
            segmentDocs.Clear();
            accumulator.Reset();
            segmentId++;
            // 段是批量处理的天然边界：主动回收上一段的临时对象（词条键 / 位置列表），
            // 避免大语料构建把 GC 堆水位推高到数百 MB。每段一次、次数 ≤ 段数。
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false);
        }

        if (segmentId > 0)
            Report(opt, PhaseWrite, (int)total, segmentId - 1);

        var manifest = new Manifest
        {
            Gen = opt.Gen,
            DocCount = (uint)total,
            LiveBytes = liveBytes,
            Anchors = new ManifestAnchors
            {
                NormalizerVersion = opt.NormalizerVersion,
                TokenizerVersion = opt.TokenizerVersion,
            },
        };
        manifest.Sections.AddRange(sections);
        FillFileBytes(manifest, EraFormat.DataStart + manifest.LiveBytes);
        file.Commit(manifest);
        CurrentPointer.Write(directory, fileName);

        return new BuildResult { Path = path, Manifest = manifest, SegmentCount = segmentId };
    }

    private static void Report(BuildOptions options, string phase, int docsDone, int segmentId) =>
        options.Progress?.Invoke(new BuildProgress(phase, docsDone, segmentId));

    /// <summary>
    /// <c>fileBytes</c> 依赖 Manifest 自身长度：反复“序列化 → 长度变化则重算”，最多 3 次。
    /// <paramref name="dataBytes"/> 是 Manifest 之前的文件长度（全量构建 = Header + 双槽 + 段体；
    /// 合并时含旧段垃圾，因此由调用方传入实际长度）。
    /// </summary>
    internal static void FillFileBytes(Manifest manifest, long dataBytes)
    {
        manifest.FileBytes = 0;
        for (var i = 0; i < 3; i++)
        {
            var predicted = dataBytes + manifest.ToJsonBytes().Length;
            if (predicted == manifest.FileBytes) break;
            manifest.FileBytes = predicted;
        }
    }

    /// <summary>
    /// 写出一个段的全部数据段（DocStore → Dict → DictIndex → Postings → 可选 Positions），
    /// 返回追加的字节数。<paramref name="terms"/> 由调用方按编码键排序后提供（合并路径）。
    /// </summary>
    internal static long WriteSegment(
        IndexFile file,
        IReadOnlyList<DocRecord> docs,
        IReadOnlyList<(byte[] Key, List<PostingEntry> Entries)> terms,
        ushort segmentId,
        List<SectionRef> sections)
    {
        long postingCount = 0;
        long positionCount = 0;
        foreach (var (_, entries) in terms)
        {
            postingCount += entries.Count;
            foreach (var entry in entries) positionCount += entry.Tf;
        }
        return WriteSegmentCore(file, docs, terms.Count, postingCount, positionCount, (dict, postings, positions, writer) =>
        {
            foreach (var (key, entries) in terms)
            {
                var offset = writer.Write(postings, positions, entries);
                dict.AddKey((FieldId)key[0], key.AsSpan(1), (uint)entries.Count, offset, DictFlagHasPositions);
            }
        }, segmentId, sections);
    }

    /// <summary>并行数组形式的段写入（全量构建热路径，避免每条 posting 一个数组对象）。</summary>
    private static long WriteSegment(
        IndexFile file,
        IReadOnlyList<DocRecord> docs,
        IReadOnlyList<(byte[] Key, CompactPostings Postings)> terms,
        ushort segmentId,
        List<SectionRef> sections)
    {
        long postingCount = 0;
        long positionCount = 0;
        foreach (var (_, compact) in terms)
        {
            postingCount += compact.Docs.Count;
            positionCount += compact.PosFlat.Count;
        }
        return WriteSegmentCore(file, docs, terms.Count, postingCount, positionCount, (dict, postings, positions, writer) =>
        {
            foreach (var (key, compact) in terms)
            {
                var offset = writer.Write(
                    postings,
                    positions,
                    CollectionsMarshal.AsSpan(compact.Docs),
                    CollectionsMarshal.AsSpan(compact.Tfs),
                    CollectionsMarshal.AsSpan(compact.PosStart),
                    CollectionsMarshal.AsSpan(compact.PosCount),
                    CollectionsMarshal.AsSpan(compact.PosFlat),
                    hasPositions: compact.HasPositions);
                dict.AddKey(
                    (FieldId)key[0],
                    key.AsSpan(1),
                    (uint)compact.Docs.Count,
                    offset,
                    compact.HasPositions ? DictFlagHasPositions : (byte)0);
            }
        }, segmentId, sections);
    }

    private static long WriteSegmentCore(
        IndexFile file,
        IReadOnlyList<DocRecord> docs,
        int termCount,
        long postingCount,
        long positionCount,
        Action<TermDictBuilder, ByteWriter, ByteWriter, PostingsWriter> writeTerms,
        ushort segmentId,
        List<SectionRef> sections)
    {
        var bytes = Append(file, SectionKind.DocStore, DocStoreWriter.Write(docs), (uint)docs.Count, segmentId, sections);

        // 按预计编码大小预分配，避免 ByteWriter 反复倍增产生大对象垃圾（构建 RSS 的主要来源）。
        var dict = new TermDictBuilder();
        var postings = new ByteWriter(Estimate(postings: true, postingCount, termCount));
        var positions = new ByteWriter(Estimate(postings: false, positionCount, termCount));
        writeTerms(dict, postings, positions, new PostingsWriter());
        var (dictBody, indexBody) = dict.Build();

        bytes += Append(file, SectionKind.Dict, dictBody, 0, segmentId, sections);
        bytes += Append(file, SectionKind.DictIndex, indexBody, 0, segmentId, sections);
        bytes += Append(file, SectionKind.Postings, postings.ToArray(), 0, segmentId, sections);
        if (positions.Length > 0)
            bytes += Append(file, SectionKind.Positions, positions.ToArray(), 0, segmentId, sections);
        return bytes;
    }

    /// <summary>
    /// 段体容量预估：doc 增量按 1–3B / posting，位置按 1–2B / 位置，另加每 128 条一块的块头。
    /// 宁可略高，低估只会退化为原有的倍增行为。
    /// </summary>
    private static int Estimate(bool postings, long count, int termCount)
    {
        var perItem = postings ? 2 : 2;
        var blocks = (count / EraFormat.PostingsBlockDocs) + 1;
        var blockHeader = postings ? 34 : 12;
        var size = (count * perItem) + (blocks * blockHeader) + (termCount * 8L) + 64;
        return (int)Math.Clamp(size, 256, int.MaxValue);
    }

    private static long Append(
        IndexFile file, SectionKind kind, byte[] body, uint docs, ushort segmentId, List<SectionRef> sections)
    {
        var appended = file.AppendSection(kind, body, docs);
        sections.Add(new SectionRef
        {
            Kind = appended.Kind,
            SegmentId = segmentId,
            Offset = appended.Offset,
            Length = appended.Length,
            Crc = appended.Crc,
            Docs = appended.Docs,
        });
        return appended.Length;
    }

    /// <summary>把 <see cref="AnalyzedDoc"/> 转成 DocStore 记录并校验；缺失字段写 0 / 空串。</summary>
    private static DocRecord MakeRecord(AnalyzedDoc doc)
    {
        if (doc.Track is null || doc.Track.Length != EraFormat.TrackIdBytes)
            throw new ArgumentException($"track 必须是 {EraFormat.TrackIdBytes} 字节", nameof(doc));
        if (doc.Rev is null || doc.Rev.Length != EraFormat.TrackIdBytes)
            throw new ArgumentException($"rev 必须是 {EraFormat.TrackIdBytes} 字节", nameof(doc));
        if (doc.Tokens is null)
            throw new ArgumentException("tokens 不能为 null", nameof(doc));

        var record = new DocRecord
        {
            Track = doc.Track,
            Rev = doc.Rev,
            ScriptBits = doc.ScriptBits,
            Flags = doc.Flags,
            Title = doc.Title ?? string.Empty,
            Artist = doc.Artist ?? string.Empty,
            Album = doc.Album ?? string.Empty,
        };

        foreach (var (field, raw) in doc.Tokens)
        {
            var index = (int)field - 1;
            if (index < 0 || index >= record.DocLen.Length)
                throw new ArgumentException($"字段 {field} 超出 1..12", nameof(doc));
            if (raw is null)
                throw new ArgumentException($"字段 {field} 的 token 数组为 null", nameof(doc));

            var count = 0;
            foreach (var term in raw)
            {
                if (term is null)
                    throw new ArgumentException("token 不能为 null", nameof(doc));
                count++;
            }
            // doc_len 是位置空间长度：空 token 也占位（与 Postings 的数组下标语义一致）。
            if (count > ushort.MaxValue)
                throw new ArgumentException($"字段 {field} token 数超过 {ushort.MaxValue}", nameof(doc));
            record.DocLen[index] = (ushort)count;
        }
        return record;
    }

    /// <summary>
    /// 段内倒排累加器：<c>(field, term) → 紧凑 posting 序列</c>。
    /// 枚举顺序不参与输出——写出前按编码键（<c>field_id(1B) || utf8(term)</c>）显式排序。
    /// </summary>
    private sealed class SegmentAccumulator
    {
        private readonly Dictionary<(FieldId Field, string Term), CompactPostings> _terms = new();

        /// <summary>倒排累加的估算内存（落段判定用，含 List 容量与对象头余量）。</summary>
        public long EstimatedBytes { get; private set; }

        /// <summary>把一条 doc 的 token 序列直接摊进词条倒排（位置 = 数组下标）。</summary>
        public void Add(int docId, IReadOnlyDictionary<FieldId, string[]> tokens, HashSet<FieldId> noPositions)
        {
            foreach (var (field, raw) in tokens)
            {
                var withPositions = !noPositions.Contains(field);
                for (var pos = 0; pos < raw.Length; pos++)
                {
                    var term = raw[pos];
                    if (term.Length == 0) continue;
                    var key = (field, term);
                    if (!_terms.TryGetValue(key, out var postings))
                    {
                        postings = new CompactPostings(withPositions);
                        _terms[key] = postings;
                        EstimatedBytes += 160 + (term.Length * 2);
                    }
                    postings.Add(docId, pos);
                    EstimatedBytes += withPositions ? 12 : 8;
                }
            }
        }

        /// <summary>按编码键字节序输出（确定性要求，不依赖字典枚举顺序）。</summary>
        public List<(byte[] Key, CompactPostings Postings)> SortedTerms()
        {
            var list = new List<(byte[] Key, CompactPostings Postings)>(_terms.Count);
            foreach (var (key, postings) in _terms)
            {
                var termBytes = Encoding.UTF8.GetBytes(key.Term);
                var encoded = new byte[1 + termBytes.Length];
                encoded[0] = (byte)key.Field;
                termBytes.CopyTo(encoded, 1);
                list.Add((encoded, postings));
            }
            list.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
            return list;
        }

        /// <summary>段写完立即释放累加器（内存约束）。</summary>
        public void Reset()
        {
            _terms.Clear();
            EstimatedBytes = 0;
        }
    }

    /// <summary>
    /// 段内一个词条的紧凑倒排：并行列表 + 连续位置数组。
    /// 调用方保证同一词条的 doc 递增、同 doc 内位置递增。
    /// </summary>
    private sealed class CompactPostings
    {
        public readonly List<int> Docs = new(1);
        public readonly List<int> Tfs = new(1);
        public readonly List<int> PosStart = new(1);
        public readonly List<int> PosCount = new(1);
        public readonly List<int> PosFlat = new(1);

        /// <summary>该词条是否带位置（同一字段族内一致）。</summary>
        public readonly bool HasPositions;

        public CompactPostings(bool hasPositions)
        {
            HasPositions = hasPositions;
        }

        public void Add(int doc, int pos)
        {
            if (Docs.Count > 0 && Docs[^1] == doc)
            {
                Tfs[^1]++;
                if (HasPositions)
                {
                    PosCount[^1]++;
                    PosFlat.Add(pos);
                }
                return;
            }
            Docs.Add(doc);
            Tfs.Add(1);
            PosStart.Add(HasPositions ? PosFlat.Count : 0);
            PosCount.Add(HasPositions ? 1 : 0);
            if (HasPositions) PosFlat.Add(pos);
        }
    }
}
