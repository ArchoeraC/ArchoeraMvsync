// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Text.Json;

namespace Archoera.Indexer.EraStore;

/// <summary>Manifest 中的段引用（偏移一律是文件内绝对偏移，§3.3.0.1）。</summary>
internal sealed class SectionRef
{
    /// <summary>段类型；未知类型在解析时跳过。</summary>
    public SectionKind Kind { get; init; }
    /// <summary>段体起始绝对偏移。</summary>
    public ulong Offset { get; init; }
    /// <summary>段体长度（字节）。</summary>
    public uint Length { get; init; }
    /// <summary>段体 CRC32C。</summary>
    public uint Crc { get; init; }
    /// <summary>
    /// 段内文档数（非 DocStore 段可为 0）；<see cref="SectionKind.Tombs"/> 段为死行数，
    /// 供死行比例估算免读段体。
    /// </summary>
    public uint Docs { get; init; }
    /// <summary>段号（0 起）；单段老数据缺失该字段时按 0 处理。</summary>
    public ushort SegmentId { get; init; }
}

/// <summary>版本锚（§3.8.2.6）：index_format / normalizer / tokenizer / scheme / data_pack。</summary>
internal sealed class ManifestAnchors
{
    public int IndexFormat = 1;
    public int NormalizerVersion = 1;
    public int TokenizerVersion = 2;
    public int SchemeVersion = 1;
    public string DataPackId = "";
}

/// <summary>
/// 每代索引的 Manifest（文件尾部 UTF-8 JSON，≤64 KB）。
/// 键序固定：<c>gen</c>、<c>format</c>、<c>anchors</c>、<c>sections</c>、<c>stats</c>；
/// 写入用 <see cref="Utf8JsonWriter"/>、读取用 <see cref="JsonDocument"/>（NativeAOT 安全）。
/// </summary>
internal sealed class Manifest
{
    /// <summary>段数上限；超出视为畸形。</summary>
    public const int MaxSections = 64;

    /// <summary>世代号（单调递增，≥1）。</summary>
    public ulong Gen;
    /// <summary>物理格式版本。</summary>
    public ushort Format = EraFormat.FormatVersion;
    /// <summary>版本锚。</summary>
    public ManifestAnchors Anchors = new();
    /// <summary>段列表。</summary>
    public List<SectionRef> Sections = new();
    /// <summary>文档总数。</summary>
    public uint DocCount;
    /// <summary>活数据字节数（不含垃圾与 Manifest）。</summary>
    public long LiveBytes;
    /// <summary>文件总字节数（统计口径）。</summary>
    public long FileBytes;

    /// <summary>序列化为紧凑 UTF-8 JSON（固定键序、无缩进）。</summary>
    public byte[] ToJsonBytes()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("gen", Gen);
            w.WriteNumber("format", Format);
            w.WriteStartObject("anchors");
            w.WriteNumber("indexFormat", Anchors.IndexFormat);
            w.WriteNumber("normalizer", Anchors.NormalizerVersion);
            w.WriteNumber("tokenizer", Anchors.TokenizerVersion);
            w.WriteNumber("scheme", Anchors.SchemeVersion);
            w.WriteString("dataPackId", Anchors.DataPackId ?? string.Empty);
            w.WriteEndObject();
            w.WriteStartArray("sections");
            foreach (var s in Sections)
            {
                w.WriteStartObject();
                w.WriteNumber("kind", (byte)s.Kind);
                w.WriteNumber("seg", s.SegmentId);
                w.WriteNumber("off", s.Offset);
                w.WriteNumber("len", s.Length);
                w.WriteNumber("crc", s.Crc);
                w.WriteNumber("docs", s.Docs);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("stats");
            w.WriteNumber("docs", DocCount);
            w.WriteNumber("liveBytes", LiveBytes);
            w.WriteNumber("fileBytes", FileBytes);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 解析 Manifest JSON：gen ≥ 1、format == 1、sections ≤ 64、数值非负，
    /// 未知 <see cref="SectionKind"/> 跳过；其余畸形一律抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static Manifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > EraFormat.MaxManifestBytes)
            throw new InvalidDataException("Manifest 长度超出 1..64KB");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Manifest JSON 无法解析", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Manifest 根不是对象");

            var m = new Manifest { Gen = RequireU64(root, "gen", min: 1) };
            if (!Require(root, "format").TryGetInt32(out var format) || format != EraFormat.FormatVersion)
                throw new InvalidDataException("Manifest format 不受支持");
            m.Format = (ushort)format;

            var anchors = RequireObject(root, "anchors");
            m.Anchors = new ManifestAnchors
            {
                IndexFormat = RequireInt(anchors, "indexFormat"),
                NormalizerVersion = RequireInt(anchors, "normalizer"),
                TokenizerVersion = RequireInt(anchors, "tokenizer"),
                SchemeVersion = RequireInt(anchors, "scheme"),
                DataPackId = RequireString(anchors, "dataPackId"),
            };

            var sections = Require(root, "sections");
            if (sections.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Manifest sections 不是数组");
            if (sections.GetArrayLength() > MaxSections)
                throw new InvalidDataException("Manifest sections 超过 64 条");
            foreach (var item in sections.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Manifest section 不是对象");
                var kind = RequireInt(item, "kind");
                if (kind < 0 || kind > byte.MaxValue)
                    throw new InvalidDataException("Manifest section kind 越界");
                // 未知段类型允许存在，旧读者按规则跳过。
                if (!Enum.IsDefined((SectionKind)(byte)kind)) continue;
                m.Sections.Add(new SectionRef
                {
                    Kind = (SectionKind)(byte)kind,
                    SegmentId = OptionalU16(item, "seg"),
                    Offset = RequireU64(item, "off"),
                    Length = RequireU32(item, "len"),
                    Crc = RequireU32(item, "crc"),
                    Docs = RequireU32(item, "docs"),
                });
            }

            var stats = RequireObject(root, "stats");
            m.DocCount = RequireU32(stats, "docs");
            m.LiveBytes = RequireI64(stats, "liveBytes");
            m.FileBytes = RequireI64(stats, "fileBytes");
            return m;
        }
    }

    private static JsonElement Require(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element)
            ? element
            : throw new InvalidDataException($"Manifest 缺少字段 {name}");

    private static JsonElement RequireObject(JsonElement parent, string name) =>
        Require(parent, name) is { ValueKind: JsonValueKind.Object } element
            ? element
            : throw new InvalidDataException($"Manifest 字段 {name} 不是对象");

    private static ulong RequireU64(JsonElement parent, string name, ulong min = 0) =>
        Require(parent, name).TryGetUInt64(out var value) && value >= min
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");

    private static uint RequireU32(JsonElement parent, string name) =>
        Require(parent, name).TryGetUInt32(out var value)
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");

    private static long RequireI64(JsonElement parent, string name) =>
        Require(parent, name).TryGetInt64(out var value) && value >= 0
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");

    private static int RequireInt(JsonElement parent, string name) =>
        Require(parent, name).TryGetInt32(out var value) && value >= 0
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");

    /// <summary>可选 u16：缺失按 0；存在但越界抛异常（老文件无 <c>seg</c> 也能读）。</summary>
    private static ushort OptionalU16(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return 0;
        return element.TryGetUInt16(out var value)
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");
    }

    private static string RequireString(JsonElement parent, string name) =>
        Require(parent, name) is { ValueKind: JsonValueKind.String } element
        && element.GetString() is { } value
            ? value
            : throw new InvalidDataException($"Manifest 字段 {name} 非法");
}
