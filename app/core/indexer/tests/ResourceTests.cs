// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// 资源生命周期回归：StoreReader 每次打开都必须释放 FileStream（mmap 用了
/// <c>leaveOpen: true</c>，漏掉会让索引更新 / 查询换世代时累积文件句柄）。
/// </summary>
internal static class ResourceTests
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux())
        {
            Program.Check("资源: StoreReader 句柄释放（非 Linux 跳过）", true);
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "era-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "search-1.eraindex");
            var track = new byte[16];
            var rev = new byte[16];
            for (var i = 0; i < 16; i++) { track[i] = (byte)i; rev[i] = (byte)(0x80 + i); }
            var doc = new AnalyzedDoc(
                track, rev, 0, 0,
                new Dictionary<FieldId, string[]> { [FieldId.Title] = new[] { "hello" } },
                "hello", "", "");
            IndexBuilder.Build(path, new[] { doc });

            // 预热一次，排除首次 JIT / 程序集加载带来的 fd 波动。
            using (var warm = new StoreReader(path)) _ = warm.Manifest;

            var before = CountFds();
            for (var i = 0; i < 40; i++)
            {
                using var reader = new StoreReader(path);
                _ = reader.Manifest;
            }
            var after = CountFds();
            Program.Check("资源: StoreReader 反复打开不泄漏文件句柄",
                after <= before + 2, $"fds {before} -> {after}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static int CountFds()
    {
        try
        {
            return Directory.GetFiles("/proc/self/fd").Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }
}
