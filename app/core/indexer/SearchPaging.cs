// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Archoera.Indexer;

/// <summary><c>total</c> 计数模式（D.2）：默认不计数，避免每次查询遍历全部倒排。</summary>
internal enum SearchTotalMode
{
    /// <summary>不计数（响应 <c>total=-1</c>）。</summary>
    None = 0,

    /// <summary>最多数到 <see cref="SearchPaging.CappedTotal"/>，<c>totalExact=false</c>。</summary>
    Capped = 1,

    /// <summary>完整计数（O(命中集)），<c>totalExact=true</c>。</summary>
    Exact = 2,
}

/// <summary>
/// keyset 游标（D.5 的 32B 布局在 v7 上的落地）。
///
/// <para><b>与 D.5 的差异</b>：v7 没有 EraStore 的 <c>(segment, local)</c>，因此
/// <c>doc_ref</c> 的高 32 位恒为 0、低 32 位放 EraStore 虚拟 doc id
/// （<see cref="Candidate.DocId"/>）。翻页时按 <see cref="DocId"/> 在**同一全序**里定位切片点；
/// 其余字段（kind / coverage / field / exact / proximity / bm25）只为满足游标布局与跨代诊断，
/// 不参与切片。</para>
///
/// <para><b>proximity 槽</b>（T3.11）：沿用 D.5 预留的 int16 槽，算不出
/// （<see cref="SearchRanker.ProximityUnknown"/>）写 <c>-1</c>，解码时还原为
/// <see cref="SearchRanker.ProximityUnknown"/>；布局与长度不变。</para>
/// </summary>
internal readonly record struct SearchCursor(
    long Gen,
    uint SchemeHash,
    int KindRank,
    double Coverage,
    int FieldRank,
    int ExactRank,
    int Proximity,
    float Bm25,
    long DocId);

/// <summary>
/// 分页与 <c>total</c> 的编解码（T2.10）。
///
/// <para>游标是**不透明**的 <c>base64url(32B)</c>：客户端只回传 <c>next</c>、不解析。
/// 非法游标（非 base64url / 长度不是 32 / 版本不是 1）一律在请求解析期报码 1。</para>
/// </summary>
internal static class SearchPaging
{
    /// <summary>游标固定长度（D.5）。</summary>
    public const int CursorBytes = 32;

    /// <summary>游标布局版本（D.5 的 <c>version=1</c>）。</summary>
    public const byte CursorVersion = 1;

    /// <summary><c>cursor</c> 字符串上限（D.2：≤4KB；32B 的 base64url 只有 43 字符）。</summary>
    public const int MaxCursorChars = 4096;

    /// <summary><c>capped</c> 模式的计数上限：数到 10001 即视为「10000+」。</summary>
    public const int CappedTotal = 10001;

    /// <summary>coverage 的定点缩放（0..10000 放进 2 字节）。</summary>
    private const int CoverageScale = 10000;

    /// <summary>方案 id → 32 位 FNV-1a（游标 <c>scheme_hash</c>，只用于失配检测）。</summary>
    public static uint SchemeHash(string schemeId)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(schemeId))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    /// <summary>把候选编码成游标（只用于 <c>next</c>；<c>gen</c> / <c>scheme</c> 取会话快照）。</summary>
    public static string Encode(Candidate candidate, long gen, uint schemeHash)
    {
        Span<byte> buffer = stackalloc byte[CursorBytes];
        buffer.Clear();
        buffer[0] = CursorVersion;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(1, 8), gen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(9, 4), schemeHash);
        buffer[13] = (byte)candidate.Evidence.Kind;
        var coverage = Math.Clamp(
            (int)Math.Round(candidate.Evidence.Coverage * CoverageScale), 0, CoverageScale);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14, 2), (ushort)coverage);
        buffer[16] = (byte)candidate.Evidence.Field;
        buffer[17] = (byte)Math.Clamp(candidate.Evidence.Exactness, 0, 255);
        // proximity（T3.11）：D.5 的 int16 槽。算不出写 -1；真实值最大是短语 token 数 - 1，
        // 远小于 short.MaxValue，超界只可能是异常数据，按「算不出」写 -1。
        var proximity = candidate.Evidence.Proximity is >= 0 and < short.MaxValue
            ? (short)candidate.Evidence.Proximity
            : (short)-1;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(18, 2), proximity);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(20, 4), (float)candidate.Bm25);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(24, 8), candidate.DocId);
        return ToBase64Url(buffer);
    }

    /// <summary>
    /// 解析游标；非法返回 <c>false</c>（调用方报码 1，不返回半页结果）。
    /// 只校验结构，不与当前索引比对（世代 / 方案失配由 <see cref="Render"/> 判定为 <c>restart</c>）。
    /// </summary>
    public static bool TryDecode(string text, out SearchCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxCursorChars) return false;

        byte[] raw;
        try
        {
            raw = FromBase64Url(text);
        }
        catch (FormatException)
        {
            return false;
        }
        if (raw.Length != CursorBytes || raw[0] != CursorVersion) return false;

        var span = raw.AsSpan();
        cursor = new SearchCursor(
            Gen: BinaryPrimitives.ReadInt64LittleEndian(span.Slice(1, 8)),
            SchemeHash: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(9, 4)),
            KindRank: span[13],
            Coverage: BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2)) / (double)CoverageScale,
            FieldRank: span[16],
            ExactRank: span[17],
            Proximity: ReadProximity(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(18, 2))),
            Bm25: BinaryPrimitives.ReadSingleLittleEndian(span.Slice(20, 4)),
            DocId: BinaryPrimitives.ReadInt64LittleEndian(span.Slice(24, 8)));
        return true;
    }

    /// <summary>游标 -1 = proximity 算不出，还原成哨兵值（与排序用的一致）。</summary>
    private static int ReadProximity(short raw)
        => raw < 0 ? SearchRanker.ProximityUnknown : raw;

    /// <summary>解析 <c>total</c>：缺省 / null / 字符串三值；其它一律失败（码 1）。</summary>
    public static bool TryParseTotalMode(string? value, out SearchTotalMode mode)
    {
        mode = SearchTotalMode.None;
        if (value is null) return true;
        switch (value.ToLowerInvariant())
        {
            case "none": mode = SearchTotalMode.None; return true;
            case "capped": mode = SearchTotalMode.Capped; return true;
            case "exact": mode = SearchTotalMode.Exact; return true;
            default: return false;
        }
    }

    /// <summary>没有更多结果时的切片终点（供调用方判定 <c>next=null</c>）。</summary>
    public static bool HasMore(int start, int returned, int total) => start + returned < total;

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        foreach (var c in text)
        {
            var legal = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!legal) throw new FormatException("cursor 不是 base64url");
        }
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => "",
            _ => throw new FormatException("cursor 长度非法"),
        };
        return Convert.FromBase64String(padded);
    }
}
