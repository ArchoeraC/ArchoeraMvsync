// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;

using Archoera.Indexer.EraStore;
using Microsoft.Data.Sqlite;

namespace Archoera.Indexer.Tests;

/// <summary>
/// S6：脏集必须发现「直接改 <c>library.db</c> 元数据、不碰音频文件」。
///
/// <list type="number">
///   <item><b>格式</b>：DocStore v6 段尾 <c>meta_hash</c>（u64×N）往返，含 0 / 空元数据；</item>
///   <item><b>迁移</b>：v5 / v4 / v3 段体拒绝；Manifest <c>metaVersion=4</c> 的旧索引强制全量重建；</item>
///   <item><b>合并</b>：<c>Merger</c> 从旧段原样拷贝，逐字节稳定；</item>
///   <item><b>脏集</b>：只改 title / artists / album（<c>file_mtime</c> 不变）也会被重索引，
///         且索引里是新值；无变更的第二次运行 0 脏曲 / 0 内容查询。</item>
/// </list>
/// </summary>
internal static class EraStoreMetaHashTests
{
    public static void Run()
    {
        RoundTrip();
        MergeCarriesMetaHash();
        DirtySetFromDbEdits();
        OldManifestForcesRebuild();
    }

    /* ------------------------------------------------------------------ */
    /* 1. DocStore v6 往返 / 定义                                            */
    /* ------------------------------------------------------------------ */

    private static void RoundTrip()
    {
        var emptyHash = MetaHash.Compute(null, null, null);
        var zero = MetaHash.Compute("", "", "");
        Program.Check("S6 格式: 当前 DocStore v6 / 六列",
            DocStoreWriter.Version == 6 && DocStoreWriter.TextColumns == 6,
            $"{DocStoreWriter.Version}/{DocStoreWriter.TextColumns}");
        Program.Check("S6 格式: Manifest 元数据锚已升到 v5",
            Manifest.CurrentMetaVersion == 5, Manifest.CurrentMetaVersion.ToString());
        Program.Check("S6 哈希: NULL 与空串同值且非 0",
            emptyHash == zero && emptyHash != 0, $"{emptyHash}/{zero}");

        var docs = new[]
        {
            MakeRecord(1, "晴天", """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""",
                MetaHash.Compute("晴天", """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""")),
            // 零哈希：旧夹具 / 手工构造的默认值必须原样往返，不能被当成「空元数据」重算。
            MakeRecord(2, "", "", "", 0),
            MakeRecord(3, "", "", "", ulong.MaxValue),
        };
        var body = DocStoreWriter.Write(docs);
        var reader = new DocStoreReader(body);

        Program.Check("S6 格式: 元数据哈希往返",
            reader.MetaHash(0) == docs[0].MetaHash
            && reader.MetaHash(1) == 0
            && reader.MetaHash(2) == ulong.MaxValue,
            $"{reader.MetaHash(0)}/{reader.MetaHash(1)}/{reader.MetaHash(2)}");
        Program.Check("S6 格式: 空元数据 / 零哈希都可读",
            reader.Count == 3 && reader.MetaHash(0) != 0,
            reader.Count.ToString());
        Program.Check("S6 格式: 段尾最后 8B 是最后一 doc 的哈希（无偏移表）",
            BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(body.Length - 8)) == ulong.MaxValue,
            body.Length.ToString());

        // 仅哈希不同时应只差 8B/doc：三段之间哈希列是定长追加列。
        var swapped = new[]
        {
            MakeRecord(1, "晴天", """[{"name":"周杰伦"}]""", """{"name":"叶惠美"}""", 1),
            MakeRecord(2, "", "", "", 2),
            MakeRecord(3, "", "", "", 3),
        };
        Program.Check("S6 格式: 哈希列不改变段体长度",
            DocStoreWriter.Write(swapped).Length == body.Length,
            $"{DocStoreWriter.Write(swapped).Length}/{body.Length}");

        // 旧 v5 / v4 / v3 段体一律拒绝并提示全量重建。
        foreach (var old in new ushort[] { 3, 4, 5 })
        {
            var patched = body.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(4), old);
            var message = ThrowsInvalidDataWithMessage(() => new DocStoreReader(patched));
            Program.Check($"S6 版本: v{old} 段体拒绝且提示全量重建",
                message is not null && message.Contains("重建", StringComparison.Ordinal),
                message ?? "(no throw)");
        }
    }

    /* ------------------------------------------------------------------ */
    /* 2. 合并原样搬运                                                      */
    /* ------------------------------------------------------------------ */

    private static void MergeCarriesMetaHash()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var hashes = new[] { 0UL, 1UL, ulong.MaxValue, 0x5EED_5EED_5EED_5EEDUL };
            var docs = hashes
                .Select((hash, i) => MakeDoc(i, hash))
                .ToList();
            IndexBuilder.Build(path, docs, Options(docsPerSegment: 2));
            var before = LiveMetaHashes(path);

            var result = Merger.Merge(path, new[] { 0, 1 });
            var after = LiveMetaHashes(path);

            Program.Check("S6 合并: 活 doc 数不变", result.LiveDocs == 4 && after.Count == 4,
                $"{result.LiveDocs}/{after.Count}");

            var same = before.Count == after.Count;
            foreach (var (track, hash) in before)
            {
                if (!after.TryGetValue(track, out var merged) || merged != hash) same = false;
            }
            Program.Check("S6 合并: meta_hash 逐字节一致（不重新计算）", same,
                string.Join(',', before.Keys));
            Program.Check("S6 合并: 夹具覆盖 0 / 非 0 哈希",
                before.Values.Any(h => h == 0) && before.Values.Any(h => h != 0),
                string.Join(',', before.Values));
            Program.Check("S6 合并: verify 通过", IndexVerifier.Verify(path).Ok,
                string.Join(" | ", IndexVerifier.Verify(path).Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 3. 增量脏集：直接改 DB 元数据                                        */
    /* ------------------------------------------------------------------ */

    private static void DirtySetFromDbEdits()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-meta-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var music = Path.Combine(root, "music");
            Directory.CreateDirectory(music);

            EndToEnd.CreateLibrary(db, generation: 1);
            const string artists = """[{"name":"原始歌手"}]""";
            const string album = """{"name":"原始专辑"}""";
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(conn, "t1", Path.Combine(music, "t1.mp3"), "alpha",
                    artists, album, "first lyric", 1000);
                EndToEnd.InsertTrack(conn, "t2", Path.Combine(music, "t2.mp3"), "bravo",
                    "[]", null, "second lyric", 2000);
                EndToEnd.InsertTrack(conn, "t3", Path.Combine(music, "t3.mp3"), "charlie",
                    """[{"name":"C"}]""", """{"name":"A3"}""", "third lyric", 3000);
            }

            var options = new IndexerOptions { IndexLrc = false };
            var full = Engine(db, index, full: true, options, out _);
            Program.Check("S6 脏集: 全量 3 活 doc", full.Indexed == 3 && full.EraLiveDocs == 3,
                $"{full.Indexed}/{full.EraLiveDocs}");

            // 构建侧与共享哈希定义必须一致：哈希原料是**原始 DB 文本**，不是解析后的显示值。
            var expected = MetaHash.Compute("alpha", artists, album);
            Program.Check("S6 脏集: 索引里的 meta_hash 与共享定义一致",
                Snapshot(index, "t1").MetaHash == expected,
                $"{Snapshot(index, "t1").MetaHash}/{expected}");

            // 无变更：0 脏曲、0 内容查询。
            var idle = Engine(db, index, full: false, options, out var idleQueries);
            Program.Check("S6 脏集: 无变更第二次运行 0 脏曲",
                idle.Indexed == 0 && !idle.Full && !idle.EraIncremental,
                $"indexed={idle.Indexed} full={idle.Full}");
            Program.Check("S6 脏集: 无变更第二次运行 0 内容查询",
                idleQueries == 0, idleQueries.ToString());

            // 只改 title，file_mtime 保持 1000。
            Exec(db, "UPDATE tracks SET title='zulu' WHERE id='t1'");
            var titleRun = Engine(db, index, full: false, options, out var titleQueries);
            Program.Check("S6 脏集: 只改 title 也重索引该曲",
                titleRun.Indexed == 1 && !titleRun.Full && titleQueries == 1,
                $"indexed={titleRun.Indexed} full={titleRun.Full} q={titleQueries}");
            var titleHash = MetaHash.Compute("zulu", artists, album);
            Program.Check("S6 脏集: 索引里的 title 是 DB 新值",
                Snapshot(index, "t1").Title == "zulu", Snapshot(index, "t1").Title);
            Program.Check("S6 脏集: 新 title 的哈希已写入",
                Snapshot(index, "t1").MetaHash == titleHash,
                $"{Snapshot(index, "t1").MetaHash}/{titleHash}");
            Program.Check("S6 脏集: 查询命中新 title",
                QueryHits(index, "zulu", "t1"), string.Join(',', QueryIds(index, "zulu")));
            Program.Check("S6 脏集: 旧 title 不再命中",
                !QueryHits(index, "alpha", "t1"), "alpha");

            // 只改 artists（仍不改 file_mtime）。
            const string newArtists = """[{"name":"新歌手"}]""";
            Exec(db, $"UPDATE tracks SET artists='{newArtists}' WHERE id='t1'");
            var artistRun = Engine(db, index, full: false, options, out _);
            Program.Check("S6 脏集: 只改 artists 也重索引该曲",
                artistRun.Indexed == 1 && !artistRun.Full, artistRun.Indexed.ToString());
            Program.Check("S6 脏集: 索引里的歌手显示值已更新",
                Snapshot(index, "t1").Artist == "新歌手", Snapshot(index, "t1").Artist);
            Program.Check("S6 脏集: artists 哈希用原始 JSON（非解析值）",
                Snapshot(index, "t1").MetaHash == MetaHash.Compute("zulu", newArtists, album),
                Snapshot(index, "t1").MetaHash.ToString());

            // 只改 album。
            const string newAlbum = """{"name":"新专辑"}""";
            Exec(db, $"UPDATE tracks SET album='{newAlbum}' WHERE id='t1'");
            var albumRun = Engine(db, index, full: false, options, out _);
            Program.Check("S6 脏集: 只改 album 也重索引该曲",
                albumRun.Indexed == 1 && !albumRun.Full, albumRun.Indexed.ToString());
            Program.Check("S6 脏集: 索引里的专辑显示值已更新",
                Snapshot(index, "t1").Album == "新专辑", Snapshot(index, "t1").Album);
            Program.Check("S6 脏集: album 哈希用原始文本",
                Snapshot(index, "t1").MetaHash == MetaHash.Compute("zulu", newArtists, newAlbum),
                Snapshot(index, "t1").MetaHash.ToString());

            // 三次改写之后再跑一次：仍应 0 脏曲（哈希稳定，不产生自激脏）。
            var stable = Engine(db, index, full: false, options, out var stableQueries);
            Program.Check("S6 脏集: 连续无变更仍 0 脏曲 / 0 内容查询",
                stable.Indexed == 0 && stableQueries == 0,
                $"indexed={stable.Indexed} q={stableQueries}");

            Program.Check("S6 脏集: verify 通过", Verify(index), VerifyDetail(index));
        }
        finally
        {
            SearchSession.CloseCached();
            Cleanup(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 4. 旧索引（metaVersion=4）强制全量重建                               */
    /* ------------------------------------------------------------------ */

    private static void OldManifestForcesRebuild()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "archoera-meta-v5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            var music = Path.Combine(root, "music");
            Directory.CreateDirectory(music);

            EndToEnd.CreateLibrary(db, generation: 1);
            using (var conn = EndToEnd.Open(db))
            {
                EndToEnd.InsertTrack(conn, "o1", Path.Combine(music, "o1.mp3"), "one",
                    "[]", null, "lyric one", 1);
                EndToEnd.InsertTrack(conn, "o2", Path.Combine(music, "o2.mp3"), "two",
                    "[]", null, "lyric two", 2);
            }

            var options = new IndexerOptions { IndexLrc = false };
            var full = Engine(db, index, full: true, options, out _);
            Program.Check("S6 迁移: 全量夹具 2 活 doc", full.Indexed == 2, full.Indexed.ToString());

            // 模拟 Q1 时代的 v5 索引：Manifest metaVersion 仍是 4。
            SetEraMetaVersion(EraFile(index), 4);
            var inc = Engine(db, index, full: false, options, out _);
            Program.Check("S6 迁移: metaVersion=4 的旧索引强制全量重建",
                inc.Full && inc.Indexed == 2, $"full={inc.Full} indexed={inc.Indexed}");
            Program.Check("S6 迁移: 重建后 metaVersion 回到 CurrentMetaVersion",
                EndToEnd.ReadManifest(index).MetaVersion == Manifest.CurrentMetaVersion,
                EndToEnd.ReadManifest(index).MetaVersion.ToString());
            Program.Check("S6 迁移: 重建后查询仍可用",
                QueryHits(index, "one", "o1"), string.Join(',', QueryIds(index, "one")));
        }
        finally
        {
            SearchSession.CloseCached();
            Cleanup(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static IndexerResult Engine(
        string db, string index, bool full, IndexerOptions options, out int contentQueries)
    {
        var engine = new IndexerEngine();
        var result = engine.Run(db, index, full, options);
        contentQueries = engine.ContentQueries;
        return result;
    }

    private sealed record DocSnapshot(
        bool Found, string Title, string Artist, string Album, ulong MetaHash, long Mtime);

    private static DocSnapshot Snapshot(string index, string trackId)
    {
        using var reader = new StoreReader(EraFile(index));
        foreach (var seg in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < seg.Docs.Count; doc++)
            {
                if (!seg.IsLive(doc)) continue;
                if (!string.Equals(seg.Docs.TrackId(doc), trackId, StringComparison.Ordinal)) continue;
                return new DocSnapshot(true, seg.Docs.Title(doc), seg.Docs.Artist(doc),
                    seg.Docs.Album(doc), seg.Docs.MetaHash(doc), seg.Docs.Mtime(doc));
            }
        }
        return new DocSnapshot(false, "", "", "", 0, 0);
    }

    private static List<string> QueryIds(string index, string query)
    {
        var result = SearchSession.Run(index, new SearchRequest
        {
            Query = query,
            Limit = 50,
            Fuzzy = true,
            Cache = false,
        });
        return result.Items.Select(hit => hit.TrackId).ToList();
    }

    private static bool QueryHits(string index, string query, string trackId)
    {
        try
        {
            return QueryIds(index, query).Any(id => string.Equals(id, trackId, StringComparison.Ordinal));
        }
        finally
        {
            SearchSession.CloseCached();
        }
    }

    private static Dictionary<string, ulong> LiveMetaHashes(string path)
    {
        using var reader = new StoreReader(path);
        var map = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var segment in reader.OpenPerSegment())
        {
            for (var doc = 0; doc < segment.Docs.Count; doc++)
            {
                if (!segment.IsLive(doc)) continue;
                map[segment.Docs.TrackId(doc)] = segment.Docs.MetaHash(doc);
            }
        }
        return map;
    }

    private static DocRecord MakeRecord(
        int seed, string title, string artists, string album, ulong metaHash) => new()
    {
        Track = TrackBytes(seed),
        Rev = RevBytes(seed),
        Title = title,
        Artist = artists,
        Album = album,
        TitleNorm = Analyzer.NormalizeMetadata(title),
        ArtistNorm = Analyzer.NormalizeMetadata(artists),
        AlbumNorm = Analyzer.NormalizeMetadata(album),
        MetaHash = metaHash,
    };

    private static AnalyzedDoc MakeDoc(int seed, ulong metaHash) =>
        new(TrackBytes(seed), RevBytes(seed), (ushort)(seed & 0x7), 0,
            new Dictionary<FieldId, string[]> { [FieldId.Title] = new[] { "t" + seed } },
            "t" + seed, "a" + seed, string.Empty,
            MetaHash: metaHash);

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

    private static BuildOptions Options(int docsPerSegment) => new()
    {
        DocsPerSegment = docsPerSegment,
        Gen = 1,
        FileUuid = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(),
        CreatedMs = 1_700_000_000_000,
        NormalizerVersion = 1,
        TokenizerVersion = 2,
    };

    private static void Exec(string db, string sql)
    {
        using var conn = EndToEnd.Open(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string EraDir(string index)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(index)) ?? ".", IndexerEngine.EraStoreDirName);

    private static string EraFile(string index)
    {
        var dir = EraDir(index);
        var name = CurrentPointer.Read(dir)
            ?? throw new InvalidOperationException($"EraStore CURRENT 缺失: {dir}");
        return Path.Combine(dir, name);
    }

    private static bool Verify(string index)
    {
        try
        {
            return IndexVerifier.Verify(EraDir(index)).Ok;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static string VerifyDetail(string index)
    {
        try
        {
            var result = IndexVerifier.Verify(EraDir(index));
            return string.Join(" | ", result.Errors);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return e.Message;
        }
    }

    /// <summary>改写当前 Manifest 的 metaVersion 并提交新世代（模拟旧格式索引）。</summary>
    private static void SetEraMetaVersion(string eraPath, int metaVersion)
    {
        using var file = IndexFile.Open(eraPath);
        var manifest = file.Current ?? throw new InvalidOperationException("EraStore 夹具无有效提交");
        manifest.MetaVersion = metaVersion;
        manifest.Gen = file.Gen + 1;
        IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);
    }

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
        var dir = Path.Combine(
            Path.GetTempPath(), "archoera-meta-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响断言。
        }
    }
}
