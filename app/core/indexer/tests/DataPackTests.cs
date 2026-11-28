// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Archoera.Indexer.EraStore;

namespace Archoera.Indexer.Tests;

/// <summary>
/// T4.3 数据包（§5.5.1–§5.5.6）：manifest 规范化 / id 稳定性、缺失降级、
/// 全量校验、id 变化强制重建、CLI <c>verify --data</c>。
/// </summary>
internal static class DataPackTests
{
    public static void Run()
    {
        ManifestCanonicalization();
        MissingGlossDegradesCrossLingual();
        MissingFoundationRefusesBuildAndQuery();
        VerifyAllDetectsCorruption();
        PackChangeForcesRebuild();
        CliVerifyData();
    }

    /* ------------------------------------------------------------------ */
    /* 1. manifest / id 稳定性                                             */
    /* ------------------------------------------------------------------ */

    private static void ManifestCanonicalization()
    {
        var dir = RequireDataDir();
        var pack = DataPack.FromDirectory(dir);
        Program.Check("数据包: manifest 可读", pack.Present && pack.Error is null, pack.Error);
        Program.Check("数据包: 基础表齐全", pack.FoundationOk, string.Join(',', pack.MissingFiles));
        Program.Check("数据包: 无降级能力", pack.DegradedCapabilities.Count == 0,
            string.Join(',', pack.DegradedCapabilities));
        Program.Check("数据包: id 形如 sha256:<64hex>",
            pack.Id.StartsWith("sha256:", StringComparison.Ordinal) && pack.Id.Length == 71, pack.Id);
        Program.Check("数据包: 内嵌 id == 磁盘 id", pack.Id == DataPack.Embedded.Id,
            $"{pack.Id} / {DataPack.Embedded.Id}");
        Program.Check("数据包: 8 个文件全部登记", pack.Manifest.Files.Count == 8,
            pack.Manifest.Files.Count.ToString());

        // 文件顺序（manifest 里的排列）不影响 id：canonical 按 name 排序。
        var reordered = DataPack.FromDirectory(dir).Manifest;
        reordered.Files.Reverse();
        Program.Check("数据包: manifest 文件顺序不影响 id",
            DataPackId.Compute(reordered) == pack.Id, DataPackId.Compute(reordered));

        // 内容（sha256）变化 → id 变化。
        var mutated = DataPack.FromDirectory(dir).Manifest;
        var first = mutated.Files[0];
        mutated.Files[0] = new DataPackFile
        {
            Name = first.Name,
            Sha256 = new string('0', 64),
            Bytes = first.Bytes,
            License = first.License,
        };
        Program.Check("数据包: sha256 变化 → id 变化",
            DataPackId.Compute(mutated) != pack.Id, DataPackId.Compute(mutated));

        // 只改排版（空白 / 换行）不影响 id：重新解析后 id 相同。
        var pretty = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, DataPack.ManifestFileName)))!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var reformatted = DataPackManifest.Parse(Encoding.UTF8.GetBytes(pretty));
        Program.Check("数据包: manifest 排版不影响 id",
            DataPackId.Compute(reformatted) == pack.Id);
    }

    /* ------------------------------------------------------------------ */
    /* 2. 缺 def_* → 去掉 cross_lingual，不崩                              */
    /* ------------------------------------------------------------------ */

    private static void MissingGlossDegradesCrossLingual()
    {
        var dir = CopyPack();
        try
        {
            File.Delete(Path.Combine(dir, DataPack.GlossEnToHanziFile));
            File.Delete(Path.Combine(dir, DataPack.GlossHanziToEnFile));
            var pack = DataPack.FromDirectory(dir);
            Program.Check("缺释义表: manifest 仍可读", pack.Present, pack.Error);
            Program.Check("缺释义表: 文件进 missing",
                pack.MissingFiles.Contains(DataPack.GlossEnToHanziFile)
                && pack.MissingFiles.Contains(DataPack.GlossHanziToEnFile),
                string.Join(',', pack.MissingFiles));
            Program.Check("缺释义表: 只降级 cross_lingual",
                pack.DegradedCapabilities.Count == 1
                && pack.DegradedCapabilities[0] == DataPack.CapabilityCrossLingual,
                string.Join(',', pack.DegradedCapabilities));
            Program.Check("缺释义表: 基础表仍齐全（不拒绝构建）", pack.FoundationOk);

            DataPack.TestOverride = pack;
            try
            {
                // 重新加载表：缺表应得到空表（TryOpen=false），而不是抛异常。
                HanziData.ResetTablesForTests();
                Program.Check("缺释义表: 表加载为空（不抛）",
                    HanziData.GlossHanziToEnEntryCount == 0);
                var query = Analyzer.AnalyzeQuery("love");
                Program.Check("缺释义表: 查询无跨语言候选（不崩）",
                    query.CrossLingual.Count == 0, string.Join('|', query.CrossLingual));

                // 健康页 / explain 的 `missing`：不能静默少召回。
                var health = SearchApiJson.HealthReady(
                    new StoreMeta { SchemaVersion = Manifest.CurrentMetaVersion },
                    null, pack.Health(pack.Id));
                Program.Check("缺释义表: 健康 JSON dataPack.missing 含文件",
                    health.Contains("\"missing\":[\"def_en2hanzi.tsv\"", StringComparison.Ordinal)
                    || health.Contains("\"missing\":[\"def_hanzi2en.tsv\"", StringComparison.Ordinal),
                    health);
                Program.Check("缺释义表: 健康 JSON degradedCapabilities 含 cross_lingual",
                    health.Contains("\"degradedCapabilities\":[\"cross_lingual\"]", StringComparison.Ordinal),
                    health);
                Program.Check("缺释义表: 健康 JSON 顶层 missing 含 cross_lingual",
                    health.Contains("\"missing\":[\"cross_lingual\"]", StringComparison.Ordinal),
                    health);
            }
            finally
            {
                DataPack.TestOverride = null;
                HanziData.ResetTablesForTests();
            }

            // 恢复内嵌包后表仍可用（证明降级是「按包」而非永久损坏）。
            Program.Check("恢复内嵌包: 释义表重新可用",
                HanziData.GlossHanziToEnEntryCount > 20000,
                HanziData.GlossHanziToEnEntryCount.ToString());
        }
        finally
        {
            EndToEnd.TryDelete(dir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 3. 缺基础表 → 拒绝构建 / 查询                                       */
    /* ------------------------------------------------------------------ */

    private static void MissingFoundationRefusesBuildAndQuery()
    {
        var dir = CopyPack();
        var root = NewDir();
        try
        {
            File.Delete(Path.Combine(dir, DataPack.NfkcFile));
            var pack = DataPack.FromDirectory(dir);
            Program.Check("缺 nfkc: 基础表不齐全", !pack.FoundationOk);
            Program.Check("缺 nfkc: missing 含 nfkc",
                pack.MissingFiles.Contains(DataPack.NfkcFile), string.Join(',', pack.MissingFiles));
            Program.Check("缺 nfkc: 错误可操作（含重装）",
                pack.FoundationError.Contains("重装", StringComparison.Ordinal), pack.FoundationError);

            DataPack.TestOverride = pack;
            try
            {
                var db = Path.Combine(root, "library.db");
                var index = Path.Combine(root, "search.db");
                EndToEnd.CreateLibrary(db, generation: 1);
                var buildError = Capture<InvalidDataException>(
                    () => EndToEnd.RunEngine(db, index, full: true, lrc: false));
                Program.Check("缺 nfkc: 构建拒绝（InvalidDataException）", buildError is not null);
                Program.Check("缺 nfkc: 构建错误含重装",
                    buildError?.Message.Contains("重装", StringComparison.Ordinal) == true,
                    buildError?.Message ?? "(no error)");

                var queryError = Capture<InvalidDataException>(
                    () => SearchSession.Run(index, new SearchRequest { Query = "晴天" }));
                Program.Check("缺 nfkc: 查询拒绝（InvalidDataException）", queryError is not null);
                Program.Check("缺 nfkc: 查询错误含重装",
                    queryError?.Message.Contains("重装", StringComparison.Ordinal) == true,
                    queryError?.Message ?? "(no error)");
            }
            finally
            {
                DataPack.TestOverride = null;
                SearchSession.CloseCached();
            }
        }
        finally
        {
            EndToEnd.TryDelete(dir);
            EndToEnd.TryDelete(root);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 4. verify --data：通过 + 发现损坏                                    */
    /* ------------------------------------------------------------------ */

    private static void VerifyAllDetectsCorruption()
    {
        var dir = CopyPack();
        try
        {
            var good = DataPack.FromDirectory(dir).VerifyAll(validateTables: true);
            Program.Check("verify --data: 正常数据包通过", good.Ok, string.Join(" | ", good.Errors));
            Program.Check("verify --data: 磁盘 id == 内嵌 id", good.Match,
                $"{good.Id} / {good.EmbeddedId}");
            Program.Check("verify --data: 无缺失 / 损坏",
                good.Missing.Count == 0 && good.Corrupt.Count == 0);

            using (var doc = JsonDocument.Parse(IndexVerifier.VerifyDataPack(
                DataPack.FromDirectory(dir)).ToJson()))
            {
                var root = doc.RootElement;
                Program.Check("verify --data: JSON ok=true", root.GetProperty("ok").GetBoolean());
                Program.Check("verify --data: JSON dataPack.id 非空",
                    root.GetProperty("dataPack").GetProperty("id").GetString()?.StartsWith(
                        "sha256:", StringComparison.Ordinal) == true);
            }

            // 翻转 1 字节：sha256 不符 → 校验失败并可定位文件。
            var target = Path.Combine(dir, DataPack.NfkcFile);
            var bytes = File.ReadAllBytes(target);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(target, bytes);
            var bad = DataPack.FromDirectory(dir).VerifyAll(validateTables: true);
            Program.Check("verify --data: 损坏数据包失败", !bad.Ok);
            Program.Check("verify --data: corrupt 列出 nfkc.tsv",
                bad.Corrupt.Contains(DataPack.NfkcFile), string.Join(',', bad.Corrupt));
            // id 只由 manifest 决定：改文件内容不改 manifest → id 不变，但 sha256 必须报错。
            Program.Check("verify --data: 文件内容变化不改 manifest id", bad.Match);
        }
        finally
        {
            EndToEnd.TryDelete(dir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 5. 数据包 id 变化 → 强制全量重建                                    */
    /* ------------------------------------------------------------------ */

    private static void PackChangeForcesRebuild()
    {
        var root = NewDir();
        var packDir = CopyPack();
        try
        {
            var db = Path.Combine(root, "library.db");
            var index = Path.Combine(root, "search.db");
            EndToEnd.CreateLibrary(db, generation: 1);

            var original = DataPack.Embedded.Id;
            var first = EndToEnd.RunEngine(db, index, full: true, lrc: false);
            Program.Check("数据包: 构建写入 dataPackId",
                EndToEnd.ReadManifest(index).Anchors.DataPackId == original,
                EndToEnd.ReadManifest(index).Anchors.DataPackId);
            Program.Check("数据包: verify 回显存储的 dataPackId",
                IndexVerifier.Verify(EndToEnd.EraDir(index)).DataPackId == original);

            var unchanged = EndToEnd.RunEngine(db, index, full: false, lrc: false);
            Program.Check("数据包: 未变化 → 增量（不重建）", !unchanged.Full);

            // 同一批文件、只改 manifest version → 新 id（等价于「复用文件但换表语义」）。
            RewriteVersion(packDir, "1.0.1-test");
            var changed = DataPack.FromDirectory(packDir);
            Program.Check("数据包: 版本变化 → id 变化", changed.Id != original,
                $"{changed.Id} / {original}");

            DataPack.TestOverride = changed;
            try
            {
                var rebuilt = EndToEnd.RunEngine(db, index, full: false, lrc: false);
                Program.Check("数据包: id 变化 → 强制全量重建", rebuilt.Full);
                Program.Check("数据包: 重建后写入新 id",
                    EndToEnd.ReadManifest(index).Anchors.DataPackId == changed.Id,
                    EndToEnd.ReadManifest(index).Anchors.DataPackId);
                var stable = EndToEnd.RunEngine(db, index, full: false, lrc: false);
                Program.Check("数据包: 新 id 稳定 → 增量", !stable.Full);
            }
            finally
            {
                DataPack.TestOverride = null;
                SearchSession.CloseCached();
            }
        }
        finally
        {
            EndToEnd.TryDelete(root);
            EndToEnd.TryDelete(packDir);
        }
    }

    /* ------------------------------------------------------------------ */
    /* 6. CLI 接线：verify --data / --data-dir                             */
    /* ------------------------------------------------------------------ */

    private static void CliVerifyData()
    {
        var dir = RequireDataDir();
        var stdout = new StringWriter();
        var original = Console.Out;
        int code;
        try
        {
            Console.SetOut(stdout);
            code = Archoera.Indexer.Program.Main(
                new[] { "verify", "--data", "--data-dir", dir });
        }
        finally
        {
            Console.SetOut(original);
        }
        Program.Check("verify --data: CLI 退出码 0", code == 0, code.ToString());
        var json = stdout.ToString().Trim();
        Program.Check("verify --data: stdout 是 JSON 且 ok=true",
            json.Contains("\"ok\":true", StringComparison.Ordinal)
            && json.Contains("\"dataPack\":", StringComparison.Ordinal),
            json.Length > 300 ? json[..300] : json);

        // 缺 --index 且缺 --data：参数错误。
        var stdout2 = new StringWriter();
        try
        {
            Console.SetOut(stdout2);
            code = Archoera.Indexer.Program.Main(new[] { "verify" });
        }
        finally
        {
            Console.SetOut(original);
        }
        Program.Check("verify: 缺 --index / --data 退出码 1", code == 1, code.ToString());
    }

    /* ------------------------------------------------------------------ */
    /* 辅助                                                                */
    /* ------------------------------------------------------------------ */

    private static string RequireDataDir() =>
        DataPack.FindDirectory()
        ?? throw new InvalidOperationException("找不到 app/core/indexer/data（manifest.json）");

    private static string CopyPack()
    {
        var source = RequireDataDir();
        var dir = NewDir();
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        return dir;
    }

    private static void RewriteVersion(string dir, string version)
    {
        var path = Path.Combine(dir, DataPack.ManifestFileName);
        var node = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException("manifest 不可读");
        node["version"] = version;
        File.WriteAllText(path, node.ToJsonString());
    }

    private static string NewDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "archoera-datapack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static T? Capture<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return null;
        }
        catch (T ex)
        {
            return ex;
        }
    }
}
