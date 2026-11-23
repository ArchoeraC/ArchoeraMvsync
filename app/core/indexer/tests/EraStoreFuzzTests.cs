// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;
using Archoera.Indexer.EraStore.Iterators;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P10：模糊 + 三阶段崩溃注入（设计文档 §3.3.0.16 / §3.8.2.5 / §5.3 P10）。
///
/// <para><b>模糊</b>：对 20 doc / 3 段的小索引做确定性截断与字节翻转，
/// <see cref="IndexFile.Open"/> / <see cref="IndexVerifier.Verify"/> /
/// <see cref="IndexStats.Collect"/> / <see cref="IndexDumper.Dump"/> 与五类迭代器查询
/// 只允许抛 <see cref="InvalidDataException"/> 或 <see cref="IOException"/>（报错或降级），
/// 不允许越界 / 空引用 / 访问违规等未预期异常。通过 <c>verify</c> 的变异，查询结果必须与基线一致。</para>
///
/// <para><b>崩溃注入</b>：用文件快照模拟"写段后 / 写 Manifest 后 / 写槽前（撕裂）"三个崩溃点，
/// 重开后只能看到旧 <c>gen</c> 或新 <c>gen</c>，绝不出现第三个值；旧槽仍完整时 <c>verify</c> 必须通过。</para>
/// </summary>
internal static class EraStoreFuzzTests
{
    private const int DocCount = 20;
    private const int SegmentDocs = 8;
    private const int MaxDocsPerQuery = 100;
    private const int MinimumMutations = 150;

    public static void Run()
    {
        var dir = NewDir();
        try
        {
            var baselinePath = Path.Combine(dir, "search-1.eraindex");
            var build = BuildBaseline(baselinePath);
            Program.Check("P10 夹具: 20 doc / 3 段",
                build.Manifest.DocCount == DocCount && build.SegmentCount == 3,
                $"{build.Manifest.DocCount}/{build.SegmentCount}");

            var baseline = File.ReadAllBytes(baselinePath);
            var expected = BaselineResults.Compute(baseline, build.Manifest);
            Program.Check("P10 基线: 3 段可查询", expected.SegmentCount == 3,
                expected.SegmentCount.ToString());
            Program.Check("P10 基线: verify Ok", IndexVerifier.Verify(baselinePath).Ok,
                string.Join(" | ", IndexVerifier.Verify(baselinePath).Errors));

            FuzzTruncation(dir, baseline, baselinePath, build, expected);
            FuzzFlip(dir, baseline, baselinePath, build, expected);
            CrashInjection(dir, baselinePath, build);
            SlotRotation(dir);
        }
        finally
        {
            Cleanup(dir);
        }

        CurrentPointerCases();
    }

    /* ------------------------------------------------------------------ */
    /* A. 随机截断 / 翻转                                                   */
    /* ------------------------------------------------------------------ */

    /// <summary>截断变异：长度覆盖 &lt;128 / Header / 槽 / 段中间 / 恰好段尾 / Manifest 内。</summary>
    private static void FuzzTruncation(
        string dir, byte[] baseline, string baselinePath, BuildResult build, BaselineResults expected)
    {
        var rng = new Fuzz.Rng(0xC0FFEE_2026UL);
        var lengths = BuildTruncationLengths(baseline, baselinePath, build, rng);
        Program.Check("模糊截断: 变异数 ≥150", lengths.Count >= MinimumMutations,
            lengths.Count.ToString());

        var stats = new MutationStats();
        var path = Path.Combine(dir, "mutant-trunc.eraindex");
        foreach (var length in lengths)
            GuardMutation(stats, path, Fuzz.Truncate(baseline, length), expected, "截断", $"len={length}");

        Report("模糊截断", stats);
    }

    private static List<int> BuildTruncationLengths(
        byte[] baseline, string baselinePath, BuildResult build, Fuzz.Rng rng)
    {
        var seen = new HashSet<int>();
        var lengths = new List<int>();

        void Add(int value)
        {
            var clamped = Math.Clamp(value, 0, baseline.Length);
            if (seen.Add(clamped)) lengths.Add(clamped);
        }

        // <128（短于 Header + 双槽）、Header 内、槽内、文件尾。
        foreach (var value in new[]
                 {
                     0, 1, 31, 63, 64, 65, 95, 96, 97, 126, 127, 128, 129,
                     baseline.Length - 1, baseline.Length,
                 })
            Add(value);

        // 每段：段前一字节 / 段首 / 段内 / 段尾前 / 恰好段尾。
        foreach (var section in build.Manifest.Sections)
        {
            var start = (int)section.Offset;
            var end = start + (int)section.Length;
            Add(start - 1);
            Add(start);
            Add(start + 1);
            Add(start + (int)section.Length / 2);
            Add(end - 1);
            Add(end);
        }

        // Manifest 内：起始 / 中间 / 末尾（末尾即完整文件的合法前缀）。
        var (manifestOffset, manifestLength) = ManifestRange(baselinePath);
        var manifestStart = (int)manifestOffset;
        var manifestEnd = manifestStart + (int)manifestLength;
        Add(manifestStart - 1);
        Add(manifestStart);
        Add(manifestStart + (int)manifestLength / 2);
        Add(manifestEnd - 1);
        Add(manifestEnd);

        while (lengths.Count < MinimumMutations)
            Add(rng.NextInt(baseline.Length + 1));

        return lengths;
    }

    /// <summary>翻转变异：先逐区域各来一例，再补 1–8 字节的随机偏移。</summary>
    private static void FuzzFlip(
        string dir, byte[] baseline, string baselinePath, BuildResult build, BaselineResults expected)
    {
        var rng = new Fuzz.Rng(0x5EED_F00D_2026UL);
        var cases = BuildFlipCases(baseline, baselinePath, build, rng);
        Program.Check("模糊翻转: 变异数 ≥150", cases.Count >= MinimumMutations,
            cases.Count.ToString());

        var stats = new MutationStats();
        var path = Path.Combine(dir, "mutant-flip.eraindex");
        foreach (var flipCase in cases)
        {
            var detail = $"{flipCase.Region} off={string.Join(',', flipCase.Offsets)}";
            GuardMutation(stats, path, Fuzz.Flip(baseline, flipCase.Offsets, rng), expected, "翻转", detail);
        }

        Report("模糊翻转", stats);
    }

    private static List<Fuzz.FlipCase> BuildFlipCases(
        byte[] baseline, string baselinePath, BuildResult build, Fuzz.Rng rng)
    {
        var cases = new List<Fuzz.FlipCase>();
        if (baseline.Length == 0) return cases;

        var regions = new List<(string Label, int Start, int End)>
        {
            ("header", 0, EraFormat.HeaderSize),
            ("slot0", EraFormat.CommitSlotOffset, EraFormat.CommitSlotOffset + EraFormat.CommitSlotSize),
            // 槽 0 的 reserved 字段不参与判定：单点污染后仍是有效提交，用于"通过 verify 仍与基线一致"的对照。
            ("slot0-reserved",
                EraFormat.CommitSlotOffset + 28, EraFormat.CommitSlotOffset + EraFormat.CommitSlotSize),
            // 备用槽单点污染：槽 0 仍是唯一有效提交，打开应降级到旧 gen 而不是失败。
            ("slot1", EraFormat.CommitSlotOffset + EraFormat.CommitSlotSize, EraFormat.DataStart),
        };

        var (manifestOffset, manifestLength) = ManifestRange(baselinePath);
        regions.Add(("manifest", (int)manifestOffset, (int)(manifestOffset + manifestLength)));

        // 段体逐类覆盖：DocStore / Dict / DictIndex / Postings / Positions。
        foreach (var section in build.Manifest.Sections)
        {
            var start = (int)section.Offset;
            regions.Add(($"seg{section.SegmentId}:{section.Kind}", start, start + (int)section.Length));
        }

        foreach (var (label, start, end) in regions)
        {
            if (end <= start || start >= baseline.Length) continue;
            var offset = rng.PickIn(start, Math.Min(end, baseline.Length));
            cases.Add(new Fuzz.FlipCase(new[] { offset }, label));
        }

        while (cases.Count < MinimumMutations)
        {
            var count = 1 + rng.NextInt(8);
            var offsets = new HashSet<int>();
            while (offsets.Count < count)
                offsets.Add(rng.NextInt(baseline.Length));
            cases.Add(new Fuzz.FlipCase(offsets.ToArray(), "random"));
        }

        return cases;
    }

    /* ------------------------------------------------------------------ */
    /* 变异执行与判定                                                       */
    /* ------------------------------------------------------------------ */

    private sealed class MutationStats
    {
        public int Opened;
        public int Committed;
        public int Uncommitted;
        public int Verified;
        public int VerifyFailed;
        public int Compared;
        public readonly List<string> Failures = new();
        public readonly List<string> Mismatches = new();
    }

    /// <summary>判定白名单：只允许"报错"（InvalidData / IO），其余一律失败。</summary>
    private static void GuardMutation(
        MutationStats stats, string path, byte[] bytes, BaselineResults expected, string kind, string detail)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            stats.Failures.Add($"{kind} {detail} [write] {Describe(e)}");
            return;
        }

        Manifest? manifest;
        bool hasCommit;
        try
        {
            using var file = IndexFile.Open(path);
            hasCommit = file.HasCommit;
            manifest = file.Current;
        }
        catch (Exception e)
        {
            Record(stats, e, kind, detail, "open");
            return;
        }

        stats.Opened++;
        if (hasCommit && manifest is not null) stats.Committed++;
        else stats.Uncommitted++;

        var verify = RunGuarded(stats, kind, detail, "verify", () => IndexVerifier.Verify(path));
        if (verify is not null)
        {
            if (verify.Ok) stats.Verified++;
            else stats.VerifyFailed++;
        }

        _ = RunGuarded(stats, kind, detail, "stats", () => IndexStats.Collect(path));
        _ = RunGuarded(stats, kind, detail, "dump", () => IndexDumper.Dump(path, null, null, 40));

        if (manifest is null) return;
        TryQueries(stats, bytes, manifest, expected, verify is { Ok: true }, kind, detail);
    }

    private static T? RunGuarded<T>(
        MutationStats stats, string kind, string detail, string stage, Func<T> action)
        where T : class
    {
        try
        {
            return action();
        }
        catch (Exception e)
        {
            Record(stats, e, kind, detail, stage);
            return null;
        }
    }

    /// <summary>可查询段在打开时再次校验边界；缺失 / 越界段按"降级"跳过，不构造越界 Span。</summary>
    private static void TryQueries(
        MutationStats stats,
        byte[] raw,
        Manifest manifest,
        BaselineResults expected,
        bool compare,
        string kind,
        string detail)
    {
        try
        {
            var readers = OpenReaders(raw, manifest);
            if (compare && readers.Count != expected.SegmentCount)
            {
                stats.Mismatches.Add(
                    $"{kind} {detail}: verify Ok 但可查询段数 {readers.Count} != {expected.SegmentCount}");
                return;
            }

            var queries = expected.Queries;
            foreach (var reader in readers)
            {
                for (var q = 0; q < queries.Count; q++)
                {
                    var docs = Drain(queries[q].Build(reader.Dict, reader.Postings));
                    if (!compare) continue;
                    if (!expected.TryGet(reader.Id, q, out var want))
                    {
                        stats.Mismatches.Add($"{kind} {detail}: 基线缺少 seg{reader.Id} 的查询结果");
                        continue;
                    }

                    if (docs.SequenceEqual(want))
                    {
                        stats.Compared++;
                        continue;
                    }

                    stats.Mismatches.Add(
                        $"{kind} {detail}: verify Ok 但 seg{reader.Id} {queries[q].Label} 与基线不一致");
                }
            }
        }
        catch (Exception e)
        {
            Record(stats, e, kind, detail, "query");
        }
    }

    /// <summary>记录非白名单异常；白名单异常只算"降级"，不计数。</summary>
    private static void Record(MutationStats stats, Exception e, string kind, string detail, string stage)
    {
        if (e is InvalidDataException or IOException) return;
        stats.Failures.Add($"{kind} {detail} [{stage}] {Describe(e)}");
    }

    private static void Report(string label, MutationStats stats)
    {
        Program.Check($"{label}: 只允许 InvalidDataException / IOException",
            stats.Failures.Count == 0, First(stats.Failures));
        Program.Check($"{label}: 至少一次打开成功", stats.Opened > 0, stats.Opened.ToString());
        Program.Check($"{label}: 至少一次进入提交态（stats/dump/查询）",
            stats.Committed > 0, stats.Committed.ToString());
        Program.Check($"{label}: 已发生降级（verify 失败或未提交）",
            stats.VerifyFailed > 0 || stats.Uncommitted > 0,
            $"verifyFailed={stats.VerifyFailed} uncommitted={stats.Uncommitted}");
        Program.Check($"{label}: 通过 verify 的变异查询结果与基线一致",
            stats.Verified > 0 && stats.Compared > 0 && stats.Mismatches.Count == 0,
            stats.Mismatches.Count > 0
                ? First(stats.Mismatches)
                : $"verified={stats.Verified} compared={stats.Compared}");
    }

    /* ------------------------------------------------------------------ */
    /* B. 三阶段崩溃注入（文件快照，不真的 kill 进程）                       */
    /* ------------------------------------------------------------------ */

    private static void CrashInjection(string dir, string baselinePath, BuildResult build)
    {
        var oldGen = build.Manifest.Gen;
        var newGen = oldGen + 1;
        var stage1 = Path.Combine(dir, "stage1.eraindex");
        var stage2 = Path.Combine(dir, "stage2.eraindex");
        var stage3 = Path.Combine(dir, "stage3.eraindex");
        var committed = Path.Combine(dir, "committed.eraindex");
        var rng = new Fuzz.Rng(0xBADC0DE_2026UL);

        // 1) 写段后、未写 Manifest：数据已落盘，但没有 Manifest / 槽指向它。
        File.Copy(baselinePath, stage1, overwrite: true);
        using (var file = IndexFile.Open(stage1))
        {
            for (var i = 0; i < 3; i++)
            {
                var body = new byte[150 + i * 41];
                for (var b = 0; b < body.Length; b++) body[b] = (byte)rng.NextInt(byte.MaxValue + 1);
                file.AppendSection(SectionKind.Postings, body);
            }
        }

        // 2) Manifest 已写完、槽未写：新 Manifest 成为尾部垃圾，旧槽继续有效。
        File.Copy(stage1, stage2, overwrite: true);
        var pending = CloneManifest(build.Manifest, newGen);
        var pendingJson = pending.ToJsonBytes();
        using (var stream = new FileStream(stage2, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(pendingJson);
            stream.Flush(flushToDisk: true);
        }

        // 3) 写槽过程中撕裂：目标槽前 12 字节（gen + manifest_off）变随机，长度字段仍为 0。
        File.Copy(stage2, stage3, overwrite: true);
        int target;
        using (var file = IndexFile.Open(stage3)) target = 1 - file.CurrentSlotIndex;

        var torn = new byte[12];
        for (var i = 0; i < torn.Length; i++) torn[i] = (byte)rng.NextInt(byte.MaxValue + 1);
        using (var stream = new FileStream(stage3, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Position = EraFormat.CommitSlotOffset + target * EraFormat.CommitSlotSize;
            stream.Write(torn);
            stream.Flush(flushToDisk: true);
        }

        // 4) 槽已 fsync：正常提交的新 gen。
        File.Copy(stage1, committed, overwrite: true);
        using (var file = IndexFile.Open(committed))
        {
            var manifest = CloneManifest(build.Manifest, newGen);
            IndexBuilder.FillFileBytes(manifest, file.Length);
            file.Commit(manifest);
        }

        CheckCrashSnapshot(stage1, oldGen, newGen, oldGen, "写段后");
        CheckCrashSnapshot(stage2, oldGen, newGen, oldGen, "写 Manifest 后");
        CheckCrashSnapshot(stage3, oldGen, newGen, oldGen, "写槽前（撕裂）");
        CheckCrashSnapshot(committed, oldGen, newGen, newGen, "槽已 fsync");
    }

    private static void CheckCrashSnapshot(
        string path, ulong oldGen, ulong newGen, ulong expectedGen, string label)
    {
        ulong gen = 0;
        var hasCommit = false;
        string? openError = null;
        try
        {
            using var file = IndexFile.Open(path);
            gen = file.Gen;
            hasCommit = file.HasCommit;
        }
        catch (Exception e)
        {
            openError = Describe(e);
        }

        Program.Check($"崩溃注入[{label}]: 可打开", openError is null, openError);
        Program.Check($"崩溃注入[{label}]: gen 只能是旧或新",
            hasCommit && (gen == oldGen || gen == newGen), $"gen={gen} hasCommit={hasCommit}");
        Program.Check($"崩溃注入[{label}]: gen == {expectedGen}", gen == expectedGen, gen.ToString());

        VerifyReport? report = null;
        StatsReport? stats = null;
        DumpReport? dump = null;
        string? diagError = null;
        try
        {
            report = IndexVerifier.Verify(path);
            stats = IndexStats.Collect(path);
            dump = IndexDumper.Dump(path, null, null, 100);
        }
        catch (Exception e)
        {
            diagError = Describe(e);
        }

        Program.Check($"崩溃注入[{label}]: verify/stats/dump 不崩溃", diagError is null, diagError);
        Program.Check($"崩溃注入[{label}]: verify Ok 且 gen 正确",
            report is { Ok: true } && report.Gen == expectedGen,
            report is null ? "verify 未执行" : string.Join(" | ", report.Errors));
        Program.Check($"崩溃注入[{label}]: stats gen 正确",
            stats is not null && stats.Gen == expectedGen, stats?.Gen.ToString());
        Program.Check($"崩溃注入[{label}]: dump 有词条",
            dump is { Terms.Count: > 0 }, dump?.Terms.Count.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* C. 槽轮换："只看到旧 / 新 gen"                                        */
    /* ------------------------------------------------------------------ */

    private static void SlotRotation(string dir)
    {
        var slotDir = Path.Combine(dir, "slot");
        Directory.CreateDirectory(slotDir);
        var path = Path.Combine(slotDir, "search-1.eraindex");
        BuildBaseline(path);

        var gen = 1UL;
        for (var i = 0; i < 3; i++)
        {
            var before = gen;
            gen = CommitNext(path);
            var snapshot = Path.Combine(slotDir, $"commit-{gen}.eraindex");
            File.Copy(path, snapshot, overwrite: true);

            ulong seen = 0;
            string? openError = null;
            try
            {
                using var file = IndexFile.Open(snapshot);
                seen = file.Gen;
            }
            catch (Exception e)
            {
                openError = Describe(e);
            }

            Program.Check($"槽轮换[提交{gen}]: 快照可打开", openError is null, openError);
            Program.Check($"槽轮换[提交{gen}]: gen ∈ {{提交前, 提交后}}",
                seen == before || seen == gen, $"seen={seen} before={before} after={gen}");
            Program.Check($"槽轮换[提交{gen}]: gen == 提交后", seen == gen,
                $"seen={seen} after={gen}");

            var snapshotReport = IndexVerifier.Verify(snapshot);
            Program.Check($"槽轮换[提交{gen}]: verify Ok", snapshotReport.Ok,
                string.Join(" | ", snapshotReport.Errors));
        }

        // 破坏最新槽指向的 Manifest → 回退到上一代（上一代完整，verify 仍 Ok）。
        var previous = gen - 1;
        var (manifestOffset, _) = ManifestRange(path);
        var corrupt = Path.Combine(slotDir, "corrupt-latest.eraindex");
        File.Copy(path, corrupt, overwrite: true);
        using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Position = manifestOffset;
            var value = stream.ReadByte();
            stream.Position = manifestOffset;
            stream.WriteByte((byte)(value ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        ulong fallback = 0;
        string? fallbackError = null;
        try
        {
            using var file = IndexFile.Open(corrupt);
            fallback = file.Gen;
        }
        catch (Exception e)
        {
            fallbackError = Describe(e);
        }

        Program.Check("槽轮换: 最新 Manifest 破坏后回退上一代",
            fallback == previous, fallbackError ?? $"fallback={fallback} previous={previous}");

        var fallbackReport = IndexVerifier.Verify(corrupt);
        Program.Check("槽轮换: 回退后 Verify Ok 且指向上代",
            fallbackReport.Ok && fallbackReport.Gen == previous,
            string.Join(" | ", fallbackReport.Errors));
    }

    /// <summary>在当前文件上提交 gen+1（只追加 Manifest + 覆盖旧槽，不重写段）。</summary>
    private static ulong CommitNext(string path)
    {
        using var file = IndexFile.Open(path);
        var current = file.Current ?? throw new InvalidDataException("缺少当前提交");
        var next = current.Gen + 1;
        var manifest = CloneManifest(current, next);
        IndexBuilder.FillFileBytes(manifest, file.Length);
        file.Commit(manifest);
        return next;
    }

    /* ------------------------------------------------------------------ */
    /* CURRENT 指针：未替换的临时文件 / 非法内容                             */
    /* ------------------------------------------------------------------ */

    private static void CurrentPointerCases()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            BuildBaseline(path);

            // "临时文件已写、尚未替换"：新内容只在 .CURRENT.<guid>.tmp 里。
            var temp = Path.Combine(dir, $".{CurrentPointer.FileName}.deadbeef.tmp");
            File.WriteAllText(temp, "search-2.eraindex\n");
            Program.Check("CURRENT: 未替换的临时文件被忽略",
                CurrentPointer.Read(dir) == "search-1.eraindex",
                CurrentPointer.Read(dir) ?? "null");
            Program.Check("CURRENT: 未替换时目录解析仍指旧文件",
                Path.GetFileName(IndexPaths.Resolve(dir)) == "search-1.eraindex",
                Path.GetFileName(IndexPaths.Resolve(dir)));

            // 旧内容（与旧世代一致）→ 旧文件。
            File.WriteAllText(Path.Combine(dir, CurrentPointer.FileName), "search-1.eraindex\n");
            Program.Check("CURRENT: 旧内容解析为旧文件且可校验",
                Path.GetFileName(IndexPaths.Resolve(dir)) == "search-1.eraindex"
                && IndexVerifier.Verify(dir).Ok);

            // 非法内容 → 只允许抛 InvalidDataException。
            File.WriteAllText(Path.Combine(dir, CurrentPointer.FileName), "not-a-generation\n");
            Program.Check("CURRENT: 非法内容 Read 返回 null",
                CurrentPointer.Read(dir) is null, CurrentPointer.Read(dir) ?? "null");
            Program.Check("CURRENT: 非法内容目录解析抛 InvalidDataException",
                ThrowsInvalidData(() => IndexPaths.Resolve(dir)));

            // 路径分隔符（目录穿越）同样视为非法。
            File.WriteAllText(Path.Combine(dir, CurrentPointer.FileName), "../search-1.eraindex\n");
            Program.Check("CURRENT: 含路径分隔符视为非法",
                CurrentPointer.Read(dir) is null && ThrowsInvalidData(() => IndexPaths.Resolve(dir)));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 夹具 / 读取器 / 查询                                                 */
    /* ------------------------------------------------------------------ */

    private static readonly string[][] TitleTokens =
    {
        new[] { "晴", "天" }, new[] { "娃", "娃" }, new[] { "雨", "天" }, new[] { "七", "里", "香" },
        new[] { "稻", "香" }, new[] { "love", "night" }, new[] { "moon", "light" },
        new[] { "sunny", "day" }, new[] { "夜", "曲" }, new[] { "北", "风" },
        new[] { "晴", "天", "快", "乐" }, new[] { "晚", "安" }, new[] { "heart", "beat" },
        new[] { "dream", "on" }, new[] { "爱", "的", "力", "量" }, new[] { "rain", "y" },
        new[] { "晴", "空" }, new[] { "lonely", "road" }, new[] { "月", "亮" }, new[] { "v2", "mix" },
    };

    private static readonly string[][] ArtistTokens =
    {
        new[] { "周", "杰", "伦" }, new[] { "周", "杰", "伦" }, new[] { "孙", "燕", "姿" },
        new[] { "周", "杰", "伦" }, new[] { "adele" }, new[] { "adele" }, new[] { "moon", "band" },
        new[] { "sunny", "band" }, new[] { "周", "杰", "伦" }, new[] { "北", "风", "团" },
        new[] { "周", "杰", "伦" }, new[] { "孙", "燕", "姿" }, new[] { "adele" }, new[] { "moon", "band" },
        new[] { "爱", "乐", "团" }, new[] { "rain", "band" }, new[] { "晴", "天", "乐", "队" },
        new[] { "lonely", "road" }, new[] { "月", "亮", "组" }, new[] { "v2", "mix" },
    };

    private static BuildResult BuildBaseline(string path)
    {
        var docs = new List<AnalyzedDoc>(DocCount);
        for (var i = 0; i < DocCount; i++)
        {
            var tokens = new Dictionary<FieldId, string[]>
            {
                [FieldId.Title] = TitleTokens[i],
                [FieldId.Artist] = ArtistTokens[i],
            };
            docs.Add(MakeDoc(i, tokens,
                string.Join(' ', TitleTokens[i]), string.Join(' ', ArtistTokens[i])));
        }

        return IndexBuilder.Build(path, docs, new BuildOptions
        {
            DocsPerSegment = SegmentDocs,
            Gen = 1,
            FileUuid = Enumerable.Range(0, EraFormat.FileUuidBytes)
                .Select(i => (byte)(0x60 + i)).ToArray(),
            CreatedMs = 1_777_000_000_000,
        });
    }

    private static AnalyzedDoc MakeDoc(
        int index, IReadOnlyDictionary<FieldId, string[]> tokens, string title, string artist)
    {
        var track = new byte[EraFormat.TrackIdBytes];
        var rev = new byte[EraFormat.TrackIdBytes];
        for (var i = 0; i < EraFormat.TrackIdBytes; i++)
        {
            track[i] = (byte)(index * 5 + i);
            rev[i] = (byte)(index * 13 + i * 3);
        }

        return new AnalyzedDoc(track, rev, (ushort)(index & 0x7), (byte)(index & 0x03), tokens,
            title, artist, "album" + index);
    }

    private sealed class QuerySpec
    {
        public required string Label { get; init; }
        public required Func<TermDictReader, PostingsReader, IDocIter> Build { get; init; }
    }

    private static List<QuerySpec> FixedQueries() => new()
    {
        new() { Label = "term:love", Build = (d, p) => new TermIter(d, p, FieldId.Title, "love") },
        new() { Label = "term:周", Build = (d, p) => new TermIter(d, p, FieldId.Artist, "周") },
        new() { Label = "term:未登录", Build = (d, p) => new TermIter(d, p, FieldId.Title, "未登录") },
        new() { Label = "prefix:晴", Build = (d, p) => new PrefixIter(d, p, FieldId.Title, "晴", 8) },
        new() { Label = "prefix:moo", Build = (d, p) => new PrefixIter(d, p, FieldId.Title, "moo", 8) },
        new() { Label = "prefix:zz", Build = (d, p) => new PrefixIter(d, p, FieldId.Title, "zz", 4) },
        new()
        {
            Label = "and:雨天",
            Build = (d, p) => new AndIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "雨"),
                new TermIter(d, p, FieldId.Title, "天"),
            }),
        },
        new()
        {
            Label = "and:晴+周",
            Build = (d, p) => new AndIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "晴"),
                new TermIter(d, p, FieldId.Artist, "周"),
            }),
        },
        new()
        {
            Label = "and:未登录+love",
            Build = (d, p) => new AndIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "未登录a"),
                new TermIter(d, p, FieldId.Title, "love"),
            }),
        },
        new()
        {
            Label = "or:晴|月",
            Build = (d, p) => new OrIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "晴"),
                new TermIter(d, p, FieldId.Title, "月"),
            }),
        },
        new()
        {
            Label = "or:love|rain",
            Build = (d, p) => new OrIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "love"),
                new TermIter(d, p, FieldId.Title, "rain"),
            }),
        },
        new()
        {
            Label = "or:未登录a|未登录b",
            Build = (d, p) => new OrIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "未登录a"),
                new TermIter(d, p, FieldId.Title, "未登录b"),
            }),
        },
        new()
        {
            Label = "phrase:雨 天",
            Build = (d, p) => new PhraseIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "雨"),
                new TermIter(d, p, FieldId.Title, "天"),
            }, positioned: true),
        },
        new()
        {
            Label = "phrase:周 杰 伦",
            Build = (d, p) => new PhraseIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Artist, "周"),
                new TermIter(d, p, FieldId.Artist, "杰"),
                new TermIter(d, p, FieldId.Artist, "伦"),
            }, positioned: true),
        },
        new()
        {
            Label = "phrase:love night",
            Build = (d, p) => new PhraseIter(new List<IDocIter>
            {
                new TermIter(d, p, FieldId.Title, "love"),
                new TermIter(d, p, FieldId.Title, "night"),
            }, positioned: true),
        },
    };

    private sealed class BaselineResults
    {
        public List<QuerySpec> Queries { get; } = FixedQueries();
        public int SegmentCount { get; private set; }
        private readonly Dictionary<(int Segment, int Query), List<int>> _docs = new();

        public static BaselineResults Compute(byte[] raw, Manifest manifest)
        {
            var result = new BaselineResults();
            var readers = OpenReaders(raw, manifest);
            result.SegmentCount = readers.Count;
            foreach (var reader in readers)
            {
                for (var q = 0; q < result.Queries.Count; q++)
                    result._docs[(reader.Id, q)] =
                        Drain(result.Queries[q].Build(reader.Dict, reader.Postings));
            }
            return result;
        }

        public bool TryGet(int segment, int query, out List<int> docs)
        {
            if (_docs.TryGetValue((segment, query), out var found))
            {
                docs = found;
                return true;
            }
            docs = null!;
            return false;
        }
    }

    private readonly record struct Readers(ushort Id, TermDictReader Dict, PostingsReader Postings);

    /// <summary>按 Manifest 打开可查询段；缺失或越界段跳过（降级），不制造越界 Span。</summary>
    private static List<Readers> OpenReaders(byte[] raw, Manifest manifest)
    {
        var readers = new List<Readers>();
        foreach (var segment in manifest.Sections.Select(s => s.SegmentId).Distinct().OrderBy(s => s))
        {
            var dict = Find(manifest, SectionKind.Dict, segment);
            var index = Find(manifest, SectionKind.DictIndex, segment);
            var postings = Find(manifest, SectionKind.Postings, segment);
            var positions = Find(manifest, SectionKind.Positions, segment);
            if (dict is null || index is null || postings is null) continue;
            if (!InRange(raw, dict) || !InRange(raw, index) || !InRange(raw, postings)) continue;
            if (positions is not null && !InRange(raw, positions)) continue;

            readers.Add(new Readers(
                segment,
                new TermDictReader(Body(raw, dict), Body(raw, index)),
                new PostingsReader(
                    Body(raw, postings),
                    positions is null ? ReadOnlyMemory<byte>.Empty : Body(raw, positions))));
        }
        return readers;
    }

    private static SectionRef? Find(Manifest manifest, SectionKind kind, ushort segment) =>
        manifest.Sections.FirstOrDefault(s => s.Kind == kind && s.SegmentId == segment);

    private static bool InRange(byte[] raw, SectionRef section) =>
        section.Offset >= (ulong)EraFormat.DataStart
        && section.Offset <= (ulong)raw.Length
        && section.Length <= (ulong)raw.Length - section.Offset;

    private static ReadOnlyMemory<byte> Body(byte[] raw, SectionRef section) =>
        raw.AsMemory((int)section.Offset, (int)section.Length);

    /// <summary>固定查询集合只取前 100 个 doc（防止损坏文件造成无界遍历）。</summary>
    private static List<int> Drain(IDocIter iter)
    {
        using (iter)
        {
            var docs = new List<int>();
            while (docs.Count < MaxDocsPerQuery && iter.Next()) docs.Add(iter.Current);
            return docs;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 通用小工具                                                           */
    /* ------------------------------------------------------------------ */

    private static Manifest CloneManifest(Manifest source, ulong gen) => new()
    {
        Gen = gen,
        Format = source.Format,
        Anchors = new ManifestAnchors
        {
            IndexFormat = source.Anchors.IndexFormat,
            NormalizerVersion = source.Anchors.NormalizerVersion,
            TokenizerVersion = source.Anchors.TokenizerVersion,
            SchemeVersion = source.Anchors.SchemeVersion,
            DataPackId = source.Anchors.DataPackId,
        },
        Sections = new List<SectionRef>(source.Sections),
        DocCount = source.DocCount,
        LiveBytes = source.LiveBytes,
        FileBytes = source.FileBytes,
    };

    /// <summary>当前槽指向的 Manifest 的 (offset, length)。</summary>
    private static (long Offset, uint Length) ManifestRange(string path)
    {
        using var file = IndexFile.Open(path);
        var slot = file.ReadSlot(file.CurrentSlotIndex);
        return ((long)slot.ManifestOffset, slot.ManifestLength);
    }

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

    private static string Describe(Exception e) => $"{e.GetType().Name}: {Truncate(e.Message, 300)}";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string First(List<string> failures) =>
        failures.Count == 0 ? string.Empty : Truncate(failures[0], 300);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-fuzz-" + Guid.NewGuid().ToString("N"));
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
