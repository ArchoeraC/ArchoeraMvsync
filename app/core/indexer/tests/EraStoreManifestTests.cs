// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// EraStore P3：Manifest + 双提交槽 + open/恢复（设计文档 §3.3.0.1 / §3.8.2.5）。
/// 覆盖两槽轮换与"槽前 / 槽后"崩溃点，以及 CURRENT 指针的原子替换。
/// </summary>
internal static class EraStoreManifestTests
{
    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "archoera-erastore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            EmptyFile(path);
            Lifecycle(path);
            CrashBeforeSlot(path);
            CrashAfterSlot(path);
            TornSlot(path);
            HeaderCorruption(dir);
            ManifestParseBounds();
            CurrentPointerBehavior(dir);
        }
        finally
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

    /// <summary>1. CreateNew + Open → HasCommit=false，Gen=0。</summary>
    private static void EmptyFile(string path)
    {
        using (var file = IndexFile.CreateNew(path, new byte[EraFormat.FileUuidBytes], 1_700_000_000_000))
        {
            Program.Check("新建: HasCommit=false", !file.HasCommit);
            Program.Check("新建: Gen=0", file.Gen == 0, file.Gen.ToString());
            Program.Check("新建: CurrentSlotIndex=-1", file.CurrentSlotIndex == -1,
                file.CurrentSlotIndex.ToString());
            Program.Check("新建: Current=null", file.Current is null);
        }

        Program.Check("新建: 文件长度 = 128", new FileInfo(path).Length == EraFormat.DataStart,
            new FileInfo(path).Length.ToString());

        using (var file = IndexFile.Open(path))
        {
            Program.Check("重开空文件: HasCommit=false", !file.HasCommit);
            Program.Check("重开空文件: Gen=0", file.Gen == 0, file.Gen.ToString());
        }
    }

    /// <summary>2/3. 追加两段 + Commit(gen=1/2)，校验轮换与字节回读。</summary>
    private static void Lifecycle(string path)
    {
        var body1 = Encoding.UTF8.GetBytes("docstore-body");
        var body2 = new byte[] { 1, 2, 3, 4, 5 };
        SectionRef first;
        SectionRef second;

        using (var file = IndexFile.Open(path))
        {
            first = file.AppendSection(SectionKind.DocStore, body1, docs: 1);
            second = file.AppendSection(SectionKind.Dict, body2);

            Program.Check("追加: 首段偏移 = 128", first.Offset == EraFormat.DataStart,
                first.Offset.ToString());
            Program.Check("追加: 次段偏移接续",
                second.Offset == EraFormat.DataStart + (ulong)body1.Length, second.Offset.ToString());
            Program.Check("追加: 段长与 CRC",
                first.Length == (uint)body1.Length && first.Crc == Crc32C.Compute(body1),
                $"len={first.Length} crc=0x{first.Crc:X8}");

            var manifest = new Manifest
            {
                Gen = 1,
                DocCount = 1,
                LiveBytes = body1.Length + body2.Length,
            };
            manifest.Sections.Add(first);
            manifest.Sections.Add(second);
            file.Commit(manifest);

            Program.Check("提交: HasCommit", file.HasCommit);
            Program.Check("提交: Gen=1", file.Gen == 1, file.Gen.ToString());
            Program.Check("提交: 首次写槽 0", file.CurrentSlotIndex == 0,
                file.CurrentSlotIndex.ToString());
        }

        using (var file = IndexFile.Open(path))
        {
            Program.Check("重开 gen1: Gen=1", file.Gen == 1, file.Gen.ToString());
            var manifest = file.Current;
            Program.Check("重开 gen1: Current 非空", manifest is not null);
            Program.Check("重开 gen1: 段数 2", manifest!.Sections.Count == 2,
                manifest.Sections.Count.ToString());

            var s0 = manifest.Sections[0];
            var s1 = manifest.Sections[1];
            Program.Check("重开 gen1: 段 0 元数据一致",
                s0.Kind == SectionKind.DocStore && s0.Offset == first.Offset
                && s0.Length == first.Length && s0.Crc == first.Crc && s0.Docs == 1,
                $"{s0.Kind}/{s0.Offset}/{s0.Length}/{s0.Crc:X8}/{s0.Docs}");
            Program.Check("重开 gen1: 段 1 元数据一致",
                s1.Kind == SectionKind.Dict && s1.Offset == second.Offset
                && s1.Length == second.Length && s1.Crc == second.Crc && s1.Docs == 0,
                $"{s1.Kind}/{s1.Offset}/{s1.Length}/{s1.Crc:X8}/{s1.Docs}");
            Program.Check("重开 gen1: 锚默认值",
                manifest.Format == EraFormat.FormatVersion
                && manifest.Anchors.IndexFormat == 1
                && manifest.Anchors.TokenizerVersion == 2,
                $"{manifest.Format}/{manifest.Anchors.IndexFormat}/{manifest.Anchors.TokenizerVersion}");

            var bytes = File.ReadAllBytes(path);
            Program.Check("重开 gen1: 段 0 字节回读",
                bytes.AsSpan((int)s0.Offset, (int)s0.Length).SequenceEqual(body1));
            Program.Check("重开 gen1: 段 1 字节回读",
                bytes.AsSpan((int)s1.Offset, (int)s1.Length).SequenceEqual(body2));
        }

        using (var file = IndexFile.Open(path))
        {
            var manifest = new Manifest { Gen = 2, DocCount = 1 };
            manifest.Sections.AddRange(file.Current!.Sections);
            file.Commit(manifest);
            Program.Check("提交 gen2: 写槽 1", file.CurrentSlotIndex == 1,
                file.CurrentSlotIndex.ToString());
        }

        using (var file = IndexFile.Open(path))
        {
            Program.Check("轮换: Gen=2", file.Gen == 2, file.Gen.ToString());
            var slot0 = file.ReadSlot(0);
            var slot1 = file.ReadSlot(1);
            Program.Check("轮换: 两槽 gen 分别 1/2", slot0.Gen == 1 && slot1.Gen == 2,
                $"{slot0.Gen}/{slot1.Gen}");
            Program.Check("轮换: slot_seq 递增", slot1.SlotSeq > slot0.SlotSeq,
                $"{slot0.SlotSeq} -> {slot1.SlotSeq}");
            Program.Check("轮换: 当前槽指向 gen2",
                file.CurrentSlotIndex == 1 && file.Current!.Gen == 2,
                file.CurrentSlotIndex.ToString());
        }
    }

    /// <summary>4. "槽前崩溃"：尾部未提交垃圾 / 未写槽的合法 Manifest 都被忽略。</summary>
    private static void CrashBeforeSlot(string path)
    {
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            stream.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22 });

        using (var file = IndexFile.Open(path))
        {
            Program.Check("槽前崩溃: 尾部垃圾被忽略", file.Gen == 2 && file.HasCommit,
                file.Gen.ToString());
        }

        var orphan = new Manifest { Gen = 3 };
        orphan.Sections.Add(new SectionRef
        {
            Kind = SectionKind.Stats,
            Offset = EraFormat.DataStart,
            Length = 4,
            Crc = 0,
            Docs = 0,
        });
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            stream.Write(orphan.ToJsonBytes());

        using (var file = IndexFile.Open(path))
        {
            Program.Check("槽前崩溃: 未写槽的 Manifest 被忽略",
                file.Gen == 2 && file.Current!.Gen == 2, file.Gen.ToString());
        }
    }

    /// <summary>5. "槽后崩溃"：槽已落盘 → 新 gen 生效。</summary>
    private static void CrashAfterSlot(string path)
    {
        using (var file = IndexFile.Open(path))
        {
            var manifest = new Manifest { Gen = 3, DocCount = 1 };
            manifest.Sections.AddRange(file.Current!.Sections);
            file.Commit(manifest);
            Program.Check("槽后提交: 写槽 0", file.CurrentSlotIndex == 0,
                file.CurrentSlotIndex.ToString());
        }

        using (var file = IndexFile.Open(path))
        {
            Program.Check("槽后崩溃: 新 gen 生效", file.Gen == 3 && file.Current!.Gen == 3,
                file.Gen.ToString());
        }
    }

    /// <summary>6. 撕裂当前槽（manifest_crc）→ 回退上一代 gen2。</summary>
    private static void TornSlot(string path)
    {
        var raw = File.ReadAllBytes(path);
        var crcAt = EraFormat.CommitSlotOffset + 20;
        raw[crcAt] ^= 0xFF;
        File.WriteAllBytes(path, raw);

        using (var file = IndexFile.Open(path))
        {
            Program.Check("撕裂槽: 回退 gen2", file.Gen == 2, file.Gen.ToString());
            Program.Check("撕裂槽: 回退槽 1", file.CurrentSlotIndex == 1,
                file.CurrentSlotIndex.ToString());
        }
    }

    /// <summary>7. Header CRC 损坏 / 文件过短 → InvalidDataException。</summary>
    private static void HeaderCorruption(string dir)
    {
        var bad = Path.Combine(dir, "bad-header.eraindex");
        using (IndexFile.CreateNew(bad, new byte[EraFormat.FileUuidBytes], 0))
        {
        }

        var raw = File.ReadAllBytes(bad);
        raw[60] ^= 0xFF;
        File.WriteAllBytes(bad, raw);
        Program.Check("Header CRC 损坏抛 InvalidDataException",
            ThrowsInvalidData(() => IndexFile.Open(bad).Dispose()));

        var shortPath = Path.Combine(dir, "short.eraindex");
        File.WriteAllBytes(shortPath, new byte[100]);
        Program.Check("过短文件抛 InvalidDataException",
            ThrowsInvalidData(() => IndexFile.Open(shortPath).Dispose()));
    }

    /// <summary>8. Manifest JSON 解析边界。</summary>
    private static void ManifestParseBounds()
    {
        var round = new Manifest { Gen = 7, DocCount = 3, LiveBytes = 10, FileBytes = 99 };
        round.Anchors.DataPackId = "pack";
        round.Sections.Add(new SectionRef
        {
            Kind = SectionKind.Postings,
            Offset = 256,
            Length = 32,
            Crc = 0x1234,
            Docs = 2,
        });

        var parsed = Manifest.Parse(round.ToJsonBytes());
        Program.Check("Manifest 往返: gen/统计",
            parsed.Gen == 7 && parsed.DocCount == 3 && parsed.LiveBytes == 10 && parsed.FileBytes == 99,
            $"{parsed.Gen}/{parsed.DocCount}/{parsed.LiveBytes}/{parsed.FileBytes}");
        Program.Check("Manifest 往返: 锚与段",
            parsed.Anchors.DataPackId == "pack"
            && parsed.Anchors.TokenizerVersion == 2
            && parsed.Sections.Count == 1
            && parsed.Sections[0].Kind == SectionKind.Postings
            && parsed.Sections[0].Offset == 256
            && parsed.Sections[0].Length == 32
            && parsed.Sections[0].Crc == 0x1234
            && parsed.Sections[0].Docs == 2,
            $"sections={parsed.Sections.Count}");

        var tooMany = new Manifest { Gen = 1 };
        for (var i = 0; i <= Manifest.MaxSections; i++)
        {
            tooMany.Sections.Add(new SectionRef
            {
                Kind = SectionKind.DocStore,
                Offset = EraFormat.DataStart,
                Length = 1,
                Crc = 0,
                Docs = 0,
            });
        }
        Program.Check("Manifest sections 超 64 抛异常",
            ThrowsInvalidData(() => Manifest.Parse(tooMany.ToJsonBytes())));

        Program.Check("Manifest gen=0 抛异常",
            ThrowsInvalidData(() => Manifest.Parse(new Manifest { Gen = 0 }.ToJsonBytes())));

        Program.Check("Manifest 畸形 JSON 抛异常",
            ThrowsInvalidData(() => Manifest.Parse("{\"gen\":"u8)));

        Program.Check("Manifest format=2 抛异常",
            ThrowsInvalidData(() => Manifest.Parse(
                new Manifest { Gen = 1, Format = 2 }.ToJsonBytes())));

        // 未知 kind 允许存在，旧读者跳过。
        const string unknownJson =
            "{\"gen\":1,\"format\":1,"
            + "\"anchors\":{\"indexFormat\":1,\"normalizer\":1,\"tokenizer\":2,\"scheme\":1,\"dataPackId\":\"\"},"
            + "\"sections\":[{\"kind\":99,\"off\":128,\"len\":1,\"crc\":0,\"docs\":0}],"
            + "\"stats\":{\"docs\":0,\"liveBytes\":0,\"fileBytes\":0}}";
        var unknown = Manifest.Parse(Encoding.UTF8.GetBytes(unknownJson));
        Program.Check("Manifest 未知 kind 跳过", unknown.Sections.Count == 0,
            unknown.Sections.Count.ToString());
    }

    /// <summary>9. CURRENT 指针：读写 / 原子替换 / 非法内容。</summary>
    private static void CurrentPointerBehavior(string dir)
    {
        var pointer = Path.Combine(dir, CurrentPointer.FileName);

        CurrentPointer.Write(dir, "search-8.eraindex");
        Program.Check("CURRENT 读写", CurrentPointer.Read(dir) == "search-8.eraindex",
            CurrentPointer.Read(dir) ?? "null");
        Program.Check("CURRENT 以换行结尾",
            File.ReadAllText(pointer) == "search-8.eraindex\n",
            File.ReadAllText(pointer).Replace("\n", "\\n"));

        CurrentPointer.Write(dir, "search-9.eraindex");
        Program.Check("CURRENT 二次写入原子替换",
            CurrentPointer.Read(dir) == "search-9.eraindex", CurrentPointer.Read(dir) ?? "null");

        File.WriteAllText(pointer, "not-a-pointer\n");
        Program.Check("CURRENT 非法内容返回 null", CurrentPointer.Read(dir) is null,
            CurrentPointer.Read(dir) ?? "null");

        File.Delete(pointer);
        Program.Check("CURRENT 缺失返回 null", CurrentPointer.Read(dir) is null);

        Program.Check("CURRENT 拒绝非法文件名",
            ThrowsArgument(() => CurrentPointer.Write(dir, "../search-1.eraindex")));
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
}
