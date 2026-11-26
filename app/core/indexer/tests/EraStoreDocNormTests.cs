// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// DocStore v3（T3.11）：三列归一元数据 <c>title_norm</c> / <c>artist_norm</c> / <c>album_norm</c>。
/// 覆盖归一规则（CJK / 繁简 / 全半角 / 音标 / 大小写 / 空白折叠）、全量构建往返、空字段、
/// 旧版本段体拒绝、合并逐字节搬运、增量改写 / 删除后的列值。
/// </summary>
internal static class EraStoreDocNormTests
{
    public static void Run()
    {
        NormalizationRule();
        RoundTripAndEmpty();
        VersionGate();
        MergeCarriesColumns();
        IncrementalRewriteDelete();
    }

    /// <summary>1. 归一形 = Analyzer.Normalize + 单空格折叠；语义只有 Normalize 一份。</summary>
    private static void NormalizationRule()
    {
        Program.Check("归一: CJK 原样保留词间空格",
            Analyzer.NormalizeMetadata("晴天 周杰倫") == "晴天 周杰伦",
            Analyzer.NormalizeMetadata("晴天 周杰倫"));
        Program.Check("归一: 繁转简", Analyzer.NormalizeMetadata("葉惠美") == "叶惠美",
            Analyzer.NormalizeMetadata("葉惠美"));
        Program.Check("归一: 日文异体 → 简体",
            Analyzer.NormalizeMetadata("銀河鉄道の夜") == "银河铁道の夜",
            Analyzer.NormalizeMetadata("銀河鉄道の夜"));
        Program.Check("归一: 全角 + 全角空格 → 半角 + 单空格",
            Analyzer.NormalizeMetadata("ＡＢＣ\u3000\u3000１２３") == "abc 123",
            Analyzer.NormalizeMetadata("ＡＢＣ\u3000\u3000１２３"));
        Program.Check("归一: 去音符号 + 小写",
            Analyzer.NormalizeMetadata("Beyoncé") == "beyonce",
            Analyzer.NormalizeMetadata("Beyoncé"));
        Program.Check("归一: 大小写折叠",
            Analyzer.NormalizeMetadata("HELLO World") == "hello world",
            Analyzer.NormalizeMetadata("HELLO World"));
        Program.Check("归一: 连续空白 / 换行 / 制表折叠为单空格",
            Analyzer.NormalizeMetadata("  hello \t\n  world  ") == "hello world",
            Analyzer.NormalizeMetadata("  hello \t\n  world  "));
        Program.Check("归一: 去首尾空白后与 Normalize 同形",
            Analyzer.NormalizeMetadata("  晴天 叶惠美  ") == Analyzer.Normalize("晴天 叶惠美"),
            Analyzer.NormalizeMetadata("  晴天 叶惠美  "));
        Program.Check("归一: 空串 / null / 全空白 → 空串",
            Analyzer.NormalizeMetadata("") == ""
            && Analyzer.NormalizeMetadata(null) == ""
            && Analyzer.NormalizeMetadata(" \t\n\u3000 ") == "",
            "empty");
    }

    /// <summary>2. 构建 → 读回：raw 列不变，norm 列与 NormalizeMetadata 一致；空字段为空串。</summary>
    private static void RoundTripAndEmpty()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "晴天 (Live)", "周杰倫", "葉惠美"),
                MakeDoc(2, "", "", ""),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 8));

            var raw = File.ReadAllBytes(path);
            Manifest manifest;
            using (var file = IndexFile.Open(path))
                manifest = file.Current!;
            var store = new DocStoreReader(SectionBody(raw, Section(manifest, SectionKind.DocStore, 0)));

            Program.Check("往返: 文档数", store.Count == 2, store.Count.ToString());
            Program.Check("往返: raw title 保持原文（未归一）", store.Title(0) == "晴天 (Live)", store.Title(0));
            Program.Check("往返: raw artist / album 保持原文",
                store.Artist(0) == "周杰倫" && store.Album(0) == "葉惠美",
                $"{store.Artist(0)}/{store.Album(0)}");

            var norm = store.NormSpans(0);
            Program.Check("往返: title_norm 与 Normalize 一致",
                Encoding.UTF8.GetString(norm.Title) == Analyzer.NormalizeMetadata("晴天 (Live)"),
                Encoding.UTF8.GetString(norm.Title));
            Program.Check("往返: artist_norm 繁转简",
                Encoding.UTF8.GetString(norm.Artist) == "周杰伦",
                Encoding.UTF8.GetString(norm.Artist));
            Program.Check("往返: album_norm 繁转简",
                Encoding.UTF8.GetString(norm.Album) == "叶惠美",
                Encoding.UTF8.GetString(norm.Album));

            var empty = store.NormSpans(1);
            Program.Check("往返: 空元数据 → 三个空串",
                empty.Title.Length == 0 && empty.Artist.Length == 0 && empty.Album.Length == 0,
                $"{empty.Title.Length}/{empty.Artist.Length}/{empty.Album.Length}");
            Program.Check("往返: 空 raw 列仍为空",
                store.Title(1).Length == 0 && store.Artist(1).Length == 0 && store.Album(1).Length == 0);

            // ranker 热路径：给定 (segment, doc) 读三列必须零分配（纯 span 视图）。
            var sink = 0;
            for (var i = 0; i < 16; i++) sink += Probe(store, i % store.Count);
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) sink += Probe(store, i % store.Count);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Program.Check("往返: NormSpans 每次调用零分配", allocated == 0 && sink > 0,
                $"{allocated}B / sink={sink}");

            Program.Check("往返: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>3. 旧 v1–v4 段体被拒绝，且元数据版本锚已递增（触发全量重建）。</summary>
    private static void VersionGate()
    {
        var body = DocStoreWriter.Write(new[] { MakeRecord(1, "晴天", "周杰伦", "叶惠美") });
        Program.Check("版本: 当前 DocStore v5 / 六列",
            DocStoreWriter.Version == 5 && DocStoreWriter.TextColumns == 6,
            $"{DocStoreWriter.Version}/{DocStoreWriter.TextColumns}");
        Program.Check("版本: 元数据锚 ≥ 4（旧索引走重建）",
            Manifest.CurrentMetaVersion >= 4, Manifest.CurrentMetaVersion.ToString());

        foreach (var old in new ushort[] { 1, 2, 3, 4 })
        {
            var patched = body.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(4), old);
            var message = ThrowsInvalidDataWithMessage(() => new DocStoreReader(patched));
            Program.Check($"版本: v{old} 段体拒绝且提示重建",
                message is not null && message.Contains("重建", StringComparison.Ordinal),
                message ?? "(no throw)");
        }

        // v3 版本但列数仍是旧的三列：同样拒绝，绝不按六列布局误读。
        var badColumns = body.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(badColumns.AsSpan(6), 3);
        Program.Check("版本: 列数不符拒绝",
            ThrowsInvalidDataWithMessage(() => new DocStoreReader(badColumns)) is not null);
    }

    /// <summary>4. 合并只搬物理数据：三列归一形逐字节不变。</summary>
    private static void MergeCarriesColumns()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "Beyoncé 第一", "周杰倫", "葉惠美"),
                MakeDoc(2, "  alpha   BETA ", "Singer", "Album One"),
                MakeDoc(3, "銀河鉄道の夜", "米津玄師", ""),
                MakeDoc(4, "", "", ""),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            var before = LiveNorms(path);

            var result = Merger.Merge(path, new[] { 0, 1 });
            var after = LiveNorms(path);

            Program.Check("合并: 活 doc 数不变", result.LiveDocs == 4 && after.Count == 4,
                $"{result.LiveDocs}/{after.Count}");
            var same = before.Count == after.Count;
            var detail = new StringBuilder();
            foreach (var (track, columns) in before)
            {
                if (!after.TryGetValue(track, out var merged))
                {
                    same = false;
                    detail.Append($"{track}:miss;");
                    continue;
                }
                if (!columns.Title.AsSpan().SequenceEqual(merged.Title)
                    || !columns.Artist.AsSpan().SequenceEqual(merged.Artist)
                    || !columns.Album.AsSpan().SequenceEqual(merged.Album))
                {
                    same = false;
                    detail.Append($"{track}:diff;");
                }
            }
            Program.Check("合并: 三列归一形逐字节一致", same, detail.ToString());
            Program.Check("合并: 归一形非空（夹具覆盖真实内容）",
                before.Values.Any(c => c.Title.Length > 0)
                && after.Values.Any(c => c.Title.Length == 0),
                $"{before.Count}/{after.Count}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>5. 增量改写 / 删除：新段带正确归一形，删除项不再可见，未变项保持原值。</summary>
    private static void IncrementalRewriteDelete()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "晴天", "周杰伦", "叶惠美"),
                MakeDoc(2, "旧名字", "旧歌手", "旧专辑"),
                MakeDoc(3, "将被删除", "某人", "某专辑"),
            };
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));

            var result = IncrementalUpdater.Apply(
                path,
                new[] { MakeDoc(2, "新名字 ＬＩＶＥ", "新歌手", "新專輯") },
                new[] { TrackBytes(3) },
                new IncrementalOptions { Merge = false, DocsPerSegment = 100 });

            Program.Check("增量: 新增 1 段 / 墓碑 2 行",
                result.SegmentsAdded == 1 && result.DeadDocs == 2,
                $"{result.SegmentsAdded}/{result.DeadDocs}");

            var live = LiveNorms(path);
            Program.Check("增量: 删除项不可见", !live.ContainsKey(TrackHex(3)),
                string.Join(',', live.Keys));
            Program.Check("增量: 未变项保持原归一形",
                live.TryGetValue(TrackHex(1), out var kept)
                && Encoding.UTF8.GetString(kept.Title) == "晴天"
                && Encoding.UTF8.GetString(kept.Artist) == "周杰伦"
                && Encoding.UTF8.GetString(kept.Album) == "叶惠美",
                "track1");

            var expectedTitle = Analyzer.NormalizeMetadata("新名字 ＬＩＶＥ");
            var expectedAlbum = Analyzer.NormalizeMetadata("新專輯");
            Program.Check("增量: 改写后新归一形生效",
                live.TryGetValue(TrackHex(2), out var updated)
                && Encoding.UTF8.GetString(updated.Title) == expectedTitle
                && Encoding.UTF8.GetString(updated.Album) == expectedAlbum,
                live.TryGetValue(TrackHex(2), out var u) ? Encoding.UTF8.GetString(u.Title) : "miss");
            Program.Check("增量: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    /// <summary>读取一条 doc 的三列长度（零分配探针）。</summary>
    private static int Probe(DocStoreReader store, int doc)
    {
        var norm = store.NormSpans(doc);
        return norm.Title.Length + norm.Artist.Length + norm.Album.Length;
    }

    /// <summary>读全部活 doc 的三列归一形（track hex → 字节副本）。</summary>
    private static Dictionary<string, (byte[] Title, byte[] Artist, byte[] Album)> LiveNorms(string path)
    {
        using var reader = new StoreReader(path);
        var map = new Dictionary<string, (byte[], byte[], byte[])>(StringComparer.Ordinal);
        foreach (var segment in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < segment.Docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                var norm = segment.Docs.NormSpans(doc);
                map[segment.Docs.TrackId(doc)] = (norm.Title.ToArray(), norm.Artist.ToArray(), norm.Album.ToArray());
            }
        }
        return map;
    }

    private static AnalyzedDoc MakeDoc(int seed, string title, string artist, string album) =>
        new(TrackBytes(seed), RevBytes(seed), (ushort)(seed & 0x7), (byte)(seed & 0x03),
            new Dictionary<FieldId, string[]> { [FieldId.Title] = new[] { title } },
            title, artist, album);

    private static DocRecord MakeRecord(int seed, string title, string artist, string album) => new()
    {
        Track = TrackBytes(seed),
        Rev = RevBytes(seed),
        Title = title,
        Artist = artist,
        Album = album,
        TitleNorm = Analyzer.NormalizeMetadata(title),
        ArtistNorm = Analyzer.NormalizeMetadata(artist),
        AlbumNorm = Analyzer.NormalizeMetadata(album),
    };

    private static byte[] TrackBytes(int seed)
    {
        var track = new byte[16];
        for (var i = 0; i < 16; i++) track[i] = (byte)(seed + i);
        return track;
    }

    private static byte[] RevBytes(int seed)
    {
        var rev = new byte[16];
        for (var i = 0; i < 16; i++) rev[i] = (byte)(seed * 7 + i);
        return rev;
    }

    private static string TrackHex(int seed) => Convert.ToHexString(TrackBytes(seed)).ToLowerInvariant();

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static SectionRef Section(Manifest manifest, SectionKind kind, ushort segmentId)
    {
        foreach (var section in manifest.Sections)
            if (section.Kind == kind && section.SegmentId == segmentId) return section;
        throw new InvalidOperationException($"缺少段 {kind}（seg={segmentId}）");
    }

    private static byte[] SectionBody(byte[] file, SectionRef section) =>
        file.AsSpan((int)section.Offset, (int)section.Length).ToArray();

    /// <summary>返回 InvalidDataException 的 message；未抛出返回 null。</summary>
    private static string? ThrowsInvalidDataWithMessage(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-docnorm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
