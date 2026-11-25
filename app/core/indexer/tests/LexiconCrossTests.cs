// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer.Tests;

/// <summary>
/// T4.1 策展跨语言词表（<c>data/lexicon_cross.tsv</c>）的加载期校验与查询展开。
///
/// <para>验收（RP §5.5.4）：非法行被拒绝（列数 / weight / 来源 / 许可证 / 单字源 /
/// 控制字符）、每源 ≤4 目标、CJK 目标切成 bigram 短语、查询命中与未命中两个方向。</para>
/// </summary>
internal static class LexiconCrossTests
{
    public static void Run()
    {
        Parsing();
        Validation();
        AnalyzerIntegration();
        DataPackMembership();
    }

    private static void Parsing()
    {
        var table = LexiconCross.Parse(new[]
        {
            "# comment",
            "周杰伦\tJay Chou\t0.95\tArchoera\tCC0-1.0\tnote",
            "Jay Chou\t周杰伦\t0.95\tArchoera\tCC0-1.0\tnote",
        });

        Program.Check("lexicon: 双向条目都装载", table.Count == 2, table.Count.ToString());
        Program.Check("lexicon: CJK 源命中 → 拉丁 token 短语",
            table.TryGetValue("周杰伦", out var latin) && latin[0] == "jay chou",
            string.Join("|", table.TryGetValue("周杰伦", out var l) ? l : Array.Empty<string>()));
        Program.Check("lexicon: 拉丁源命中 → CJK bigram 短语",
            table.TryGetValue("jay chou", out var cjk) && cjk[0] == "周杰 杰伦",
            string.Join("|", table.TryGetValue("jay chou", out var c) ? c : Array.Empty<string>()));
    }

    private static void Validation()
    {
        var table = LexiconCross.Parse(new[]
        {
            // 无许可证 → 拒绝
            "王菲\tFaye Wong\t0.95\tArchoera\t\tn",
            // weight 越界 → 拒绝
            "张学友\tJacky Cheung\t1.50\tArchoera\tCC0-1.0\tn",
            // 单字 CJK 源 → 拒绝
            "爱\tlove\t0.95\tArchoera\tCC0-1.0\tn",
            // 列数不对 → 拒绝
            "陈奕迅\tEason Chan\t0.95\tArchoera",
            // 合法：同源 5 个目标，按 weight 取前 4
            "五月天\tMayday\t0.99\tArchoera\tCC0-1.0\tn",
            "五月天\tMay Day\t0.98\tArchoera\tCC0-1.0\tn",
            "五月天\tMaydayband\t0.97\tArchoera\tCC0-1.0\tn",
            "五月天\tWuyuetian\t0.96\tArchoera\tCC0-1.0\tn",
            "五月天\tFifth Month\t0.95\tArchoera\tCC0-1.0\tn",
        });

        Program.Check("lexicon: 非法行被拒绝，仅保留合法源",
            table.Count == 1 && table.ContainsKey("五月天"), table.Count.ToString());
        Program.Check("lexicon: 每源最多 4 个目标",
            table.TryGetValue("五月天", out var targets) && targets.Count == 4,
            table.TryGetValue("五月天", out var t) ? t.Count.ToString() : "0");
        Program.Check("lexicon: 目标按 weight 降序",
            table.TryGetValue("五月天", out var ordered)
            && ordered[0] == "mayday" && ordered[^1] != "fifth month",
            string.Join("|", table.TryGetValue("五月天", out var o) ? o : Array.Empty<string>()));
    }

    private static void AnalyzerIntegration()
    {
        var cjk = Analyzer.AnalyzeQuery("周杰伦");
        Program.Check("lexicon 查询: 周杰伦 → 跨语言候选含 Jay Chou",
            cjk.CrossLingual.Contains("jay chou"),
            string.Join("|", cjk.CrossLingual));

        var latin = Analyzer.AnalyzeQuery("Jay Chou");
        Program.Check("lexicon 查询: Jay Chou → 候选含 周杰 杰伦 短语",
            latin.CrossLingual.Contains("周杰 杰伦"),
            string.Join("|", latin.CrossLingual));

        var negative = Analyzer.AnalyzeQuery("zzzznottaterm");
        Program.Check("lexicon 查询: 未登录词不展开（负例）",
            !negative.CrossLingual.Contains("周杰 杰伦")
            && !negative.CrossLingual.Contains("jay chou"),
            string.Join("|", negative.CrossLingual));

        var single = Analyzer.AnalyzeQuery("周");
        Program.Check("lexicon 查询: 单字不触发专名展开",
            !single.CrossLingual.Contains("jay chou"),
            string.Join("|", single.CrossLingual));
    }

    private static void DataPackMembership()
    {
        var pack = DataPack.Current;
        Program.Check("lexicon: 数据包 manifest 含 lexicon_cross.tsv",
            pack.Manifest.Files.Any(f => f.Name == LexiconCross.FileName),
            string.Join(",", pack.Manifest.Files.Select(f => f.Name)));
        Program.Check("lexicon: 数据包可打开词表",
            pack.TryOpen(LexiconCross.FileName, out var stream) && stream is not null,
            pack.Id);
        stream?.Dispose();
        Program.Check("lexicon: 生产词表已装填（≥16 个 source）",
            LexiconCross.Count >= 16, LexiconCross.Count.ToString());
    }
}
