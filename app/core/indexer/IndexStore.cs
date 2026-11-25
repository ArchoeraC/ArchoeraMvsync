// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>
/// 一篇文档分析后的词元。字段与 EraStore 的 12 个字段族一一对应；
/// 元数据（track_id / mtime / 侧车锚点）由 <see cref="EraStore.AnalyzedDoc"/> 一并写入 DocStore。
/// 供上层（<see cref="IndexerEngine"/>）构造，存储后端不感知源语义。
/// </summary>
public sealed class DocTokens
{
    /// <summary>曲库稳定标识（<c>tracks.id</c>，TEXT）。</summary>
    public string TrackId { get; init; } = "";

    /// <summary>
    /// 三层歌词置信度（T4.2 / §5.6.2）：每层 2 bit，写入 DocStore v4 的 <c>layer_conf</c> 列。
    /// 低置信层仍写倒排（<c>scope=lyrics</c> 要能搜到），只是查询期从 <c>scope=all</c> 过滤。
    /// </summary>
    public byte LayerConf { get; init; }

    /// <summary>
    /// 三层来源位（已左移到 <c>flags</c> 的 bit3–7）：原文 2 bit / 译文 2 bit / 音译 1 bit。
    /// 只用于 <c>explain.layer</c> 与健康诊断，不改变切分产物。
    /// </summary>
    public byte LayerSourceBits { get; init; }

    public string TitleTok { get; init; } = "";
    public string ArtistTok { get; init; } = "";
    public string AlbumTok { get; init; } = "";
    public string LyricsTok { get; init; } = "";
    public string LyricsTrTok { get; init; } = "";

    /// <summary>
    /// 音译歌词列：两种来源——LRC 提供的音译层（**整词**词元），或开启「歌词罗马音生成」
    /// （T2-9）且无音译层时自动生成的歌词读音 **字母 bigram**（与 <c>reading_meta_tok</c> 同构）。
    /// </summary>
    public string LyricsRomTok { get; init; } = "";
    /// <summary>
    /// 拼音列（**元数据段**，v4 起与歌词段分列）：title/artist/album/filename 的全拼音节。
    /// 拆列动机见 <see cref="PinyinLyricsTok"/>——这四列才是「元数据」查询该扫的范围。
    /// </summary>
    public string PinyinMetaTok { get; init; } = "";

    /// <summary>
    /// 拼音列（**歌词段**）：合并后的歌词（原文层）全拼音节。
    ///
    /// 为什么与元数据分列（P5 实测）：v3 把两者混在一列，于是 `scope=metadata`（列过滤白名单）
    /// 根本收不住拼音查询——词元本身就在同一列里。分列后元数据查询只扫 `<see cref="PinyinMetaTok"/>`，
    /// 「只搜标题 / 歌手」才真正成立（也顺带缩小该路径的命中集）。
    /// </summary>
    public string PinyinLyricsTok { get; init; } = "";

    /// <summary>首字母列（**元数据段**）：title/artist/album/filename 的拼音首字母串。</summary>
    public string InitialsMetaTok { get; init; } = "";

    /// <summary>首字母列（**歌词段**）：歌词原文层的拼音首字母串。</summary>
    public string InitialsLyricsTok { get; init; } = "";

    /// <summary>
    /// 日文读音列（**元数据段**，v5）：title/artist/album/filename 的假名 / 汉字读音罗马字 bigram。
    ///
    /// **只索引元数据**：歌词的罗马字检索是伪需求（用户按假名 / 汉字搜歌词），而整段歌词的读音
    /// bigram 会把索引推高约 2×（真实 165 首日文库实测 2.42MB → 4.53MB），故不索引歌词读音。
    /// </summary>
    public string ReadingMetaTok { get; init; } = "";

    /// <summary>
    /// 读音列的**原文副本**（与 <see cref="ReadingMetaTok"/> 同值）。
    ///
    /// 倒排不保存 token 原文；兜底趟需要原始读音串来区分「整词前缀命中 / 整词包含命中」（T1-4）。
    /// 副本只在元数据段，单曲数十字节，30k 曲库约 +3–6 MB，可接受。
    /// </summary>
    public string ReadingText { get; init; } = "";

    public string FilenameTok { get; init; } = "";

    /// <summary>索引时的 <c>tracks.file_mtime</c>（脏集判定的版本锚）。</summary>
    public long IndexedMtime { get; init; }

    /// <summary>索引时的 <c>.lrc</c> 侧车 mtime（无侧车 / 子开关关闭时为 0）。</summary>
    public long IndexedLrcMtime { get; init; }

    /// <summary>索引时的 <c>.lrc</c> 侧车字节大小（无侧车 / 子开关关闭时为 0）。</summary>
    public long IndexedLrcSize { get; init; }

    /// <summary>索引时观测到的扫描世代水位线。</summary>
    public long IndexedGeneration { get; init; }
}

/// <summary>
/// 查询侧 / 构建选项的强类型视图（版本 / 选项 / 语言画像）。
/// 值来自 EraStore Manifest，缺失键取保守默认值。
/// </summary>
public sealed class StoreMeta
{
    /// <summary>
    /// EraStore 查询元数据版本（与 <see cref="EraStore.Manifest.CurrentMetaVersion"/> 同源）。
    /// 健康检查的 <c>schemaVersion</c> 用它，不再表示已删除的 v7 <c>search.db</c> schema。
    /// </summary>
    public int SchemaVersion { get; set; }

    public int AnalyzerVersion { get; set; }
    public bool IndexLrc { get; set; }
    public bool Pinyin { get; set; }

    /// <summary>是否开启了「歌词罗马音生成」（T2-9）；开关变化触发全量重建。</summary>
    public bool RomajiLyrics { get; set; }

    /// <summary>
    /// 是否开启了「歌词首字母列」（T3.4 V4，默认关）；开关变化触发全量重建。
    ///
    /// 查询侧用它裁剪首字母支的列过滤：关闭时只扫 <c>initials_meta_tok</c>，
    /// 不再引用空的 <c>initials_lyrics_tok</c>。合成 / 诊断 meta 若不设该字段，
    /// 默认按关闭处理（与产品默认一致）。
    /// </summary>
    public bool InitialsLyrics { get; set; }

    public long AppliedGeneration { get; set; }
    public long BuiltAt { get; set; }
    public long FullBuildAt { get; set; }

    /// <summary>
    /// 曲库语言画像：索引内容里是否出现过对应脚本。
    /// 查询侧据此**跳过不可能命中的兜底趟**（纯拉丁库跳过读数 / 韩文趟，
    /// 没有汉字可命中的库在跨语言趟里丢掉汉字候选）。
    ///
    /// 默认 <c>true</c>（保守：缺键 / 旧库不裁剪），构建收尾时会写入实际值。
    /// </summary>
    public bool HasJapanese { get; set; } = true;

    /// <inheritdoc cref="HasJapanese"/>
    public bool HasKorean { get; set; } = true;

    /// <inheritdoc cref="HasJapanese"/>
    public bool HasHan { get; set; } = true;

    /// <inheritdoc cref="HasJapanese"/>
    public bool HasLatin { get; set; } = true;
}
