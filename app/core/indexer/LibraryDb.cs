// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Archoera.Indexer;

/// <summary>
/// 源曲库 <c>library.db</c> 的只读访问辅助（T3.13）。
///
/// <para>搜索 / 片段 / 高亮 / 降级检索都要读 <c>library.db</c>，但这不是搜索引擎索引；
/// 把连接串与 ASCII 词判定放在中立命名下，避免查询路径依赖已删除的 v7 存储实现。</para>
/// </summary>
internal static class LibraryDb
{
    /// <summary>
    /// SQLite 连接串：**禁用连接池**。索引 / 曲库会被原子替换（tmp → 正式名），
    /// 池里残留的句柄会指向已 unlink 的旧 inode（见 EndToEnd 的连续全量回归）。
    /// </summary>
    public static string ConnectionString(string path, bool readOnly = false)
        => readOnly
            ? $"Data Source={path};Mode=ReadOnly;Pooling=False"
            : $"Data Source={path};Pooling=False";

    /// <summary>只收 ASCII 小写 / 数字（模糊词表 / 拼音音节判定）。</summary>
    public static bool IsAsciiWord(string term)
    {
        if (term.Length == 0) return false;
        foreach (var c in term)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) continue;
            return false;
        }
        return true;
    }
}
