// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// N20「构建进行中读取旧快照」的并发集成回归。
///
/// <para><b>合同</b>：EraStore 全量重建先写新文件、完成后再原子切换 <c>CURRENT</c>，
/// 最后清理旧文件。因此在构建进行中以及构建完成后，生产查询路径（
/// <see cref="SearchSession"/>）与已打开的 <see cref="StoreReader"/> 都必须读到
/// **完整**快照（旧或新），既不能读到半成品，也不能因旧文件被清理而失效。</para>
///
/// <para><b>确定性</b>：用构建进度回调在 <c>commit</c> 阶段（新文件写盘之前）阻塞后台
/// 构建线程，主线程此刻查询；释放后 <see cref="System.Threading.Thread.Join(TimeSpan)"/>
/// 等构建线程结束再断言新快照。不使用 sleep，也不靠轮询观测竞态。</para>
/// </summary>
internal static class SearchConcurrencyTests
{
    public static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-search-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            BuildWhileQuerying(root);
        }
        finally
        {
            SearchSession.CloseCached();
            EndToEnd.TryDelete(root);
        }
    }

    private static void BuildWhileQuerying(string root)
    {
        var db = Path.Combine(root, "library.db");
        var index = Path.Combine(root, "search.db");
        var audio = Path.Combine(root, "audio");
        Directory.CreateDirectory(audio);

        EndToEnd.CreateLibrary(db, generation: 1);
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "c1", Song(audio, "c1.mp3"), "共同歌一", "[]", null,
                "共同歌词一", 1000);
            EndToEnd.InsertTrack(conn, "c2", Song(audio, "c2.mp3"), "共同歌二", "[]", null,
                "共同歌词二", 2000);
        }

        var initial = Build(db, index, progress: null);
        Program.Check("N20 初始构建 2 条", initial.Indexed == 2, initial.Indexed.ToString());

        var eraDir = Path.Combine(root, IndexerEngine.EraStoreDirName);
        var gen1Name = EraStore.CurrentPointer.Read(eraDir)
            ?? throw new InvalidOperationException("初始构建未写 CURRENT");
        var gen1Path = Path.Combine(eraDir, gen1Name);

        // 构建前打开的长生命周期句柄：跨整个重建存活。
        using var oldReader = new StoreReader(gen1Path);
        Program.Check("N20 旧 reader 初始 2 文档",
            oldReader.TotalDocs == 2, oldReader.TotalDocs.ToString());
        Program.Check("N20 旧 reader 初始查询 2 命中",
            LiteralHits(oldReader, "共同") == 2, LiteralHits(oldReader, "共同").ToString());

        var before = Query(index, "共同");
        Program.Check("N20 构建前查询 2 命中", Ids(before) == "c1,c2", Ids(before));

        // 新增第 3 首：下一次全量重建会写新文件 + 切换 CURRENT。
        using (var conn = EndToEnd.Open(db))
        {
            EndToEnd.InsertTrack(conn, "c3", Song(audio, "c3.mp3"), "共同歌三", "[]", null,
                "共同歌词三", 3000);
        }

        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IndexerResult? rebuilt = null;
        Exception? buildError = null;

        var build = new Thread(() =>
        {
            try
            {
                var engine = new IndexerEngine(progress =>
                {
                    if (!string.Equals(progress.Phase, "commit", StringComparison.Ordinal)) return;
                    reached.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                });
                rebuilt = engine.Run(db, index, full: true, new IndexerOptions());
            }
            catch (Exception ex)
            {
                buildError = ex;
                reached.Set();
            }
        })
        {
            IsBackground = true,
            Name = "n20-build",
        };
        build.Start();

        // 构建线程停在 commit 阶段：新文件尚未写入，CURRENT 仍指向旧快照。
        Program.Check("N20 后台构建停在 commit 阶段",
            reached.Wait(TimeSpan.FromSeconds(30)), "timeout");

        var duringName = EraStore.CurrentPointer.Read(eraDir);
        Program.Check("N20 构建中 CURRENT 仍指向旧快照",
            string.Equals(duringName, gen1Name, StringComparison.Ordinal), duringName ?? "(null)");

        var during = Query(index, "共同");
        Program.Check("N20 构建中查询读旧快照（2 命中）", Ids(during) == "c1,c2", Ids(during));
        Program.Check("N20 构建中旧 reader 仍完整",
            oldReader.TotalDocs == 2 && LiteralHits(oldReader, "共同") == 2,
            $"{oldReader.TotalDocs}/{LiteralHits(oldReader, "共同")}");

        release.Set();
        var joined = build.Join(TimeSpan.FromSeconds(60));
        Program.Check("N20 构建线程正常结束", joined && buildError is null,
            buildError?.Message ?? (joined ? "ok" : "timeout"));
        Program.Check("N20 重建结果 3 条", rebuilt?.Indexed == 3,
            rebuilt?.Indexed.ToString() ?? "(null)");

        // 新会话：必须看到新快照（CURRENT 已切换）。
        SearchSession.CloseCached();
        var gen2Name = EraStore.CurrentPointer.Read(eraDir);
        Program.Check("N20 构建后 CURRENT 切到新快照",
            gen2Name is not null && !string.Equals(gen2Name, gen1Name, StringComparison.Ordinal),
            gen2Name ?? "(null)");

        var after = Query(index, "共同");
        Program.Check("N20 构建后查询新快照（3 命中）", Ids(after) == "c1,c2,c3", Ids(after));

        // 旧句柄：旧文件已被清理，但已打开的 reader 仍是完整旧快照（Linux / Windows 均成立：
        // 前者 unlink 后 fd 仍可读，后者清理失败保留文件）。
        Program.Check("N20 旧 reader 跨重建仍 2 文档",
            oldReader.TotalDocs == 2, oldReader.TotalDocs.ToString());
        Program.Check("N20 旧 reader 跨重建仍 2 命中",
            LiteralHits(oldReader, "共同") == 2, LiteralHits(oldReader, "共同").ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static IndexerResult Build(
        string db, string index, Action<IndexerProgress>? progress)
    {
        var engine = new IndexerEngine(progress);
        return engine.Run(db, index, full: true, new IndexerOptions());
    }

    /// <summary>走生产渐进查询路径（与服务端默认方案一致）。</summary>
    private static SearchResult Query(string index, string query)
        => SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Limit = 50,
            Fuzzy = true,
            MinExact = 1,
        });

    /// <summary>直接对 EraStore 快照执行字面 term（不经过会话缓存）。</summary>
    private static int LiteralHits(StoreReader reader, string term)
        => EraQueryPlan.Execute(reader, "\"" + term + "\"", 0)
            .Select(candidate => candidate.TrackIdHex)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static string Ids(SearchResult result)
        => string.Join(',', result.Items
            .Select(hit => hit.TrackId)
            .OrderBy(id => id, StringComparer.Ordinal));

    private static string Song(string dir, string name) => Path.Combine(dir, name);
}
