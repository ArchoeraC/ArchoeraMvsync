// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P6：合成语料 + 流式构建 + 进度回调（设计文档 §3.8.2.1 / §5.3）。
/// 覆盖多段（segment）模型、段内倒排与位置、字节确定性、进度阶段与 50k 曲压力。
/// </summary>
internal static class EraStoreBuilderTests
{
    public static void Run()
    {
        SmallCorpusEndToEnd();
        MultiSegment();
        ByteDeterminism();
        ProgressCallbacks();
        LargeCorpus();
        ManifestSegmentCompatibility();
        TokenRules();
        EmptyInput();
    }

    /// <summary>1. 3 曲端到端：Manifest / DocStore / 词典 / 倒排 / 位置。</summary>
    private static void SmallCorpusEndToEnd()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "晴天 娃娃", "周杰伦",
                    (FieldId.Title, new[] { "晴天", "娃娃" }),
                    (FieldId.Artist, new[] { "周杰伦" })),
                MakeDoc(2, "晴天", "周杰伦",
                    (FieldId.Title, new[] { "晴天" }),
                    (FieldId.Artist, new[] { "周杰伦", "翻唱" })),
                MakeDoc(3, "雨天", "孙燕姿",
                    (FieldId.Title, new[] { "雨天" }),
                    (FieldId.Artist, new[] { "孙燕姿" })),
            };

            var result = IndexBuilder.Build(path, docs, Options());
            Program.Check("构建: SegmentCount=1", result.SegmentCount == 1, result.SegmentCount.ToString());
            Program.Check("构建: FileBytes 与实际文件长度一致",
                result.Manifest.FileBytes == new FileInfo(path).Length,
                $"{result.Manifest.FileBytes}/{new FileInfo(path).Length}");

            var raw = File.ReadAllBytes(path);
            Manifest manifest;
            using (var file = IndexFile.Open(path))
            {
                Program.Check("构建: 已提交且 gen=1", file.HasCommit && file.Gen == 1,
                    file.Gen.ToString());
                manifest = file.Current!;
            }
            Program.Check("构建: DocCount=3", manifest.DocCount == 3, manifest.DocCount.ToString());
            Program.Check("构建: CURRENT 指向新文件",
                CurrentPointer.Read(dir) == "search-1.eraindex", CurrentPointer.Read(dir) ?? "null");

            var sections = manifest.Sections;
            var docStore = new DocStoreReader(SectionBody(raw, Section(sections, SectionKind.DocStore, 0)));
            Program.Check("DocStore: 段内 3 条", docStore.Count == 3, docStore.Count.ToString());
            Program.Check("DocStore: track 与输入一致",
                docStore.Track(0).SequenceEqual(docs[0].Track)
                && docStore.Track(1).SequenceEqual(docs[1].Track)
                && docStore.Track(2).SequenceEqual(docs[2].Track));
            Program.Check("DocStore: title / artist 原文",
                docStore.Title(0) == "晴天 娃娃" && docStore.Title(1) == "晴天"
                && docStore.Title(2) == "雨天"
                && docStore.Artist(0) == "周杰伦" && docStore.Artist(2) == "孙燕姿");
            Program.Check("DocStore: doc_len = 非空 token 数",
                docStore.DocLen(0, FieldId.Title) == 2 && docStore.DocLen(0, FieldId.Artist) == 1
                && docStore.DocLen(2, FieldId.Title) == 1,
                $"{docStore.DocLen(0, FieldId.Title)}/{docStore.DocLen(0, FieldId.Artist)}");

            var dict = DictReader(raw, sections, 0);
            var postings = new PostingsReader(
                SectionBody(raw, Section(sections, SectionKind.Postings, 0)),
                SectionBody(raw, Section(sections, SectionKind.Positions, 0)));

            var found = dict.TryFind(FieldId.Title, "晴天", out var qing);
            var qingPostings = found ? postings.DecodeAll(qing.PostOffset, qing.Df) : new List<PostingEntry>();
            Program.Check("词典: 晴天 df=2 且带位置",
                found && qing.Df == 2 && qing.Flags == 1, found ? $"df={qing.Df}" : "miss");
            Program.Check("倒排: 晴天 命中 doc 0/1",
                qingPostings.Count == 2 && qingPostings[0].Doc == 0 && qingPostings[1].Doc == 1,
                string.Join(',', qingPostings.Select(x => x.Doc)));
            Program.Check("倒排: 晴天 位置 [0]",
                qingPostings.Count == 2 && qingPostings[0].Positions is [0]
                && qingPostings[1].Positions is [0]);

            var foundWawa = dict.TryFind(FieldId.Title, "娃娃", out var wawa);
            var wawaPostings = foundWawa ? postings.DecodeAll(wawa.PostOffset, wawa.Df) : new List<PostingEntry>();
            Program.Check("倒排: 娃娃 位置 [1]",
                foundWawa && wawaPostings.Count == 1 && wawaPostings[0].Positions is [1]);

            var foundZhou = dict.TryFind(FieldId.Artist, "周杰伦", out var zhou);
            var zhouPostings = foundZhou ? postings.DecodeAll(zhou.PostOffset, zhou.Df) : new List<PostingEntry>();
            Program.Check("词典: artist 周杰伦 df=2", foundZhou && zhou.Df == 2, "miss");
            Program.Check("倒排: 周杰伦 第二 doc 位置 [0]",
                zhouPostings.Count == 2 && zhouPostings[1].Positions is [0]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>2. 5 曲 / 每段 2 曲 → 3 段；段号与段内词典隔离。</summary>
    private static void MultiSegment()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(1, "w0", "a", (FieldId.Title, new[] { "w0" })),
                MakeDoc(2, "w1", "a", (FieldId.Title, new[] { "w1" })),
                MakeDoc(3, "w2", "a", (FieldId.Title, new[] { "w2" })),
                MakeDoc(4, "w3", "a", (FieldId.Title, new[] { "w3" })),
                MakeDoc(5, "w4 zeta", "a", (FieldId.Title, new[] { "w4", "zeta" })),
            };

            var result = IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            Program.Check("多段: SegmentCount=3", result.SegmentCount == 3, result.SegmentCount.ToString());
            Program.Check("多段: DocCount=5", result.Manifest.DocCount == 5, result.Manifest.DocCount.ToString());

            var raw = File.ReadAllBytes(path);
            Manifest manifest;
            using (var file = IndexFile.Open(path))
                manifest = file.Current!;
            var sections = manifest.Sections;

            var ids = sections.Select(x => x.SegmentId).Distinct().OrderBy(x => x).ToArray();
            Program.Check("多段: SegmentId 依次 0/1/2",
                ids.SequenceEqual(new ushort[] { 0, 1, 2 }), string.Join(',', ids));

            int Count(ushort seg) =>
                new DocStoreReader(SectionBody(raw, Section(sections, SectionKind.DocStore, seg))).Count;
            Program.Check("多段: DocStore 计数 2/2/1",
                Count(0) == 2 && Count(1) == 2 && Count(2) == 1,
                $"{Count(0)}/{Count(1)}/{Count(2)}");

            var dict0 = DictReader(raw, sections, 0);
            var dict1 = DictReader(raw, sections, 1);
            var dict2 = DictReader(raw, sections, 2);
            Program.Check("多段: zeta 只出现在第 3 段",
                !dict0.TryFind(FieldId.Title, "zeta", out _)
                && !dict1.TryFind(FieldId.Title, "zeta", out _)
                && dict2.TryFind(FieldId.Title, "zeta", out var zeta) && zeta.Df == 1);
            var d0w0 = dict0.TryFind(FieldId.Title, "w0", out _);
            var d0w2 = dict0.TryFind(FieldId.Title, "w2", out _);
            var d1w2 = dict1.TryFind(FieldId.Title, "w2", out _);
            var d1w4 = dict1.TryFind(FieldId.Title, "w4", out _);
            Program.Check("多段: 段内词典不串段",
                d0w0 && !d0w2 && d1w2 && !d1w4, $"{d0w0}/{d0w2}/{d1w2}/{d1w4}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>3. 相同输入 + 固定 uuid/createdMs/gen → 两文件逐字节一致。</summary>
    private static void ByteDeterminism()
    {
        var dirA = NewDir();
        var dirB = NewDir();
        try
        {
            var docs = new List<AnalyzedDoc>
            {
                MakeDoc(11, "晴天", "周杰伦", (FieldId.Title, new[] { "晴天" })),
                MakeDoc(12, "娃娃", "周杰伦", (FieldId.Title, new[] { "娃娃" })),
                MakeDoc(13, "晴天 娃娃", "某歌手",
                    (FieldId.Title, new[] { "晴天", "娃娃" }),
                    (FieldId.Artist, new[] { "某歌手" })),
            };

            var pathA = Path.Combine(dirA, "search-1.eraindex");
            var pathB = Path.Combine(dirB, "search-1.eraindex");
            IndexBuilder.Build(pathA, docs, Options());
            IndexBuilder.Build(pathB, docs, Options());

            var bytesA = File.ReadAllBytes(pathA);
            var bytesB = File.ReadAllBytes(pathB);
            Program.Check("确定性: 两次构建逐字节一致",
                bytesA.AsSpan().SequenceEqual(bytesB), $"{bytesA.Length}/{bytesB.Length}");
        }
        finally
        {
            Cleanup(dirA);
            Cleanup(dirB);
        }
    }

    /// <summary>4. 进度回调：analyze 每 50 曲、write 每段，收尾 DocsDone = 总数。</summary>
    private static void ProgressCallbacks()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var events = new List<BuildProgress>();
            var options = Options();
            options.Progress = events.Add;
            var docs = Enumerable.Range(0, 120)
                .Select(i => MakeDoc(i + 1, "t" + i, "a", (FieldId.Title, new[] { "w" + i })))
                .ToList();

            IndexBuilder.Build(path, docs, options);

            var analyze = events.Where(e => e.Phase == "analyze").Select(e => e.DocsDone).ToArray();
            Program.Check("进度: 出现 analyze 阶段", analyze.Length > 0);
            Program.Check("进度: 出现 write 阶段",
                events.Any(e => e.Phase == "write" && e.SegmentId == 0));
            Program.Check("进度: analyze 每 50 曲",
                analyze.SequenceEqual(new[] { 50, 100 }), string.Join(',', analyze));
            var last = events[^1];
            Program.Check("进度: 结束 write DocsDone=120",
                last.Phase == "write" && last.DocsDone == 120 && last.SegmentId == 0,
                $"{last.Phase}/{last.DocsDone}/{last.SegmentId}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>5. 50k 曲 / 16k 每段 → 4 段；纯内存拼装 + 顺序写盘，不得 OOM。</summary>
    private static void LargeCorpus()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var result = IndexBuilder.Build(path, LargeCorpus(50_000), Options(docsPerSegment: 16_000));

            Program.Check("50k: DocCount=50000", result.Manifest.DocCount == 50_000,
                result.Manifest.DocCount.ToString());
            Program.Check("50k: SegmentCount=4", result.SegmentCount == 4, result.SegmentCount.ToString());

            using (var file = IndexFile.Open(path))
                Program.Check("50k: 回读 gen / doc 数",
                    file.Gen == 1 && file.Current is not null && file.Current.DocCount == 50_000,
                    file.Gen.ToString());
            Program.Check("50k: FileBytes 与实际文件长度一致",
                result.Manifest.FileBytes == new FileInfo(path).Length,
                $"{result.Manifest.FileBytes}/{new FileInfo(path).Length}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>6. Manifest 兼容：<c>seg</c> 缺失按 0；越界拒绝。</summary>
    private static void ManifestSegmentCompatibility()
    {
        const string legacyJson =
            "{\"gen\":1,\"format\":1,"
            + "\"anchors\":{\"indexFormat\":1,\"normalizer\":1,\"tokenizer\":2,\"scheme\":1,\"dataPackId\":\"\"},"
            + "\"sections\":[{\"kind\":1,\"off\":128,\"len\":10,\"crc\":0,\"docs\":1}],"
            + "\"stats\":{\"docs\":1,\"liveBytes\":10,\"fileBytes\":210}}";
        var legacy = Manifest.Parse(Encoding.UTF8.GetBytes(legacyJson));
        Program.Check("Manifest 兼容: 缺 seg 按 0",
            legacy.Sections.Count == 1 && legacy.Sections[0].SegmentId == 0);

        var withSeg = new Manifest { Gen = 1 };
        withSeg.Sections.Add(new SectionRef
        {
            Kind = SectionKind.DocStore,
            Offset = 128,
            Length = 1,
            Crc = 0,
            Docs = 1,
            SegmentId = 7,
        });
        var json = withSeg.ToJsonBytes();
        Program.Check("Manifest: seg 键写入", Encoding.UTF8.GetString(json).Contains("\"seg\":7"));
        var parsed = Manifest.Parse(json);
        Program.Check("Manifest: seg 往返",
            parsed.Sections.Count == 1 && parsed.Sections[0].SegmentId == 7,
            parsed.Sections.Count == 1 ? parsed.Sections[0].SegmentId.ToString() : "miss");

        const string badSeg =
            "{\"gen\":1,\"format\":1,"
            + "\"anchors\":{\"indexFormat\":1,\"normalizer\":1,\"tokenizer\":2,\"scheme\":1,\"dataPackId\":\"\"},"
            + "\"sections\":[{\"kind\":1,\"seg\":70000,\"off\":128,\"len\":1,\"crc\":0,\"docs\":1}],"
            + "\"stats\":{\"docs\":1,\"liveBytes\":1,\"fileBytes\":1}}";
        Program.Check("Manifest: seg 超过 ushort 抛异常",
            ThrowsInvalidData(() => Manifest.Parse(Encoding.UTF8.GetBytes(badSeg))));
    }

    /* ------------------------------------------------------------------ */

    /// <summary>7. token 规则：空串跳过但仍占位置，null 直接拒绝。</summary>
    private static void TokenRules()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var doc = MakeDoc(1, "a", "x", (FieldId.Title, new[] { "a", "", "a" }));
            var result = IndexBuilder.Build(path, new[] { doc }, Options());

            var raw = File.ReadAllBytes(path);
            var sections = result.Manifest.Sections;
            var dict = DictReader(raw, sections, 0);
            var postings = new PostingsReader(
                SectionBody(raw, Section(sections, SectionKind.Postings, 0)),
                SectionBody(raw, Section(sections, SectionKind.Positions, 0)));
            var found = dict.TryFind(FieldId.Title, "a", out var a);
            var list = found ? postings.DecodeAll(a.PostOffset, a.Df) : new List<PostingEntry>();
            Program.Check("token: 空串跳过且仍占位置",
                found && a.Df == 1 && list.Count == 1 && list[0].Tf == 2
                && list[0].Positions is [0, 2],
                found ? $"df={a.Df}" : "miss");

            var bad = MakeDoc(2, "b", "x", (FieldId.Title, new[] { "b", null! }));
            Program.Check("token: null 抛 ArgumentException",
                ThrowsArgument(() => IndexBuilder.Build(
                    Path.Combine(dir, "search-2.eraindex"), new[] { bad }, Options())));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>8. 空输入：0 段、DocCount=0，仍正常提交。</summary>
    private static void EmptyInput()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var result = IndexBuilder.Build(path, Array.Empty<AnalyzedDoc>(), Options());
            Program.Check("空输入: SegmentCount=0", result.SegmentCount == 0,
                result.SegmentCount.ToString());
            Program.Check("空输入: DocCount=0 且无段",
                result.Manifest.DocCount == 0 && result.Manifest.Sections.Count == 0,
                result.Manifest.Sections.Count.ToString());

            using (var file = IndexFile.Open(path))
                Program.Check("空输入: 已提交且 FileBytes 准确",
                    file.HasCommit && file.Gen == 1
                    && result.Manifest.FileBytes == new FileInfo(path).Length,
                    file.Gen.ToString());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */

    private static BuildOptions Options(int docsPerSegment = 16_000) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static AnalyzedDoc MakeDoc(
        int seed, string title, string artist, params (FieldId Field, string[] Tokens)[] fields)
    {
        var track = new byte[16];
        var rev = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            track[i] = (byte)(seed + i);
            rev[i] = (byte)(seed * 7 + i);
        }

        var tokens = new Dictionary<FieldId, string[]>();
        foreach (var (field, terms) in fields) tokens[field] = terms;

        return new AnalyzedDoc(track, rev, (ushort)(seed & 0x7), (byte)(seed & 0x03), tokens,
            title, artist, "album" + seed);
    }

    /// <summary>50k 合成语料：每曲 4 个分散词元，惰性生成避免整库驻留。</summary>
    private static IEnumerable<AnalyzedDoc> LargeCorpus(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var track = new byte[16];
            var rev = new byte[16];
            for (var b = 0; b < 16; b++)
            {
                track[b] = (byte)(i + b);
                rev[b] = (byte)(i * 3 + b);
            }

            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = new[] { "w" + i, "x" + i, "y" + i, "z" + i },
            };
            yield return new AnalyzedDoc(track, rev, 0, 0, tokens, "t" + i, "a" + i, string.Empty);
        }
    }

    private static TermDictReader DictReader(byte[] raw, List<SectionRef> sections, ushort segmentId) =>
        new(SectionBody(raw, Section(sections, SectionKind.Dict, segmentId)),
            SectionBody(raw, Section(sections, SectionKind.DictIndex, segmentId)));

    private static SectionRef Section(List<SectionRef> sections, SectionKind kind, ushort segmentId)
    {
        foreach (var section in sections)
            if (section.Kind == kind && section.SegmentId == segmentId) return section;
        throw new InvalidOperationException($"缺少段 {kind}（seg={segmentId}）");
    }

    private static byte[] SectionBody(byte[] file, SectionRef section) =>
        file.AsSpan((int)section.Offset, (int)section.Length).ToArray();

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static bool ThrowsArgument(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-builder-" + Guid.NewGuid().ToString("N"));
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
