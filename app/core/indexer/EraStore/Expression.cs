// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace Archoera.Indexer.EraStore;

/// <summary>FTS5 MATCH 表达式节点（S3 / T3.0 的 EraStore 等价实现）。</summary>
internal abstract record FtsNode;

/// <summary>单个引号词（可带前缀 <c>*</c>）。</summary>
internal sealed record FtsTerm(string Token, bool Prefix) : FtsNode;

/// <summary>引号内的多 token 短语（FTS5 位置短语；<see cref="PrefixLast"/> 表示末词前缀）。</summary>
internal sealed record FtsPhrase(IReadOnlyList<string> Tokens, bool PrefixLast) : FtsNode;

/// <summary>AND 组合（≥2 个子节点）。</summary>
internal sealed record FtsAnd(IReadOnlyList<FtsNode> Children) : FtsNode;

/// <summary>OR 组合（≥2 个子节点）。</summary>
internal sealed record FtsOr(IReadOnlyList<FtsNode> Children) : FtsNode;

/// <summary>列作用域 <c>{col ...} : expr</c>；嵌套作用域取交集（与 FTS5 一致）。</summary>
internal sealed record FtsScope(IReadOnlyList<string> Columns, FtsNode Child) : FtsNode;

/// <summary>
/// <see cref="SearchQuery"/> 产出的 FTS5 MATCH 表达式解析器（S3 / T3.0）。
///
/// <para>只覆盖编译器实际生成的语法子集：<c>{col ...} :</c> 列作用域、双引号词 / 短语
/// （内部 <c>""</c> 转义、可带 <c>*</c> 前缀）、<c>AND</c> / <c>OR</c>、圆括号。
/// 没有 <c>NOT</c> / <c>NEAR</c> / 位置修饰符。</para>
///
/// <para>优先级与 FTS5 一致：列作用域绑定紧随其后的一个 primary（比 AND / OR 更紧），
/// 例如 <c>{a} : "x" AND "y"</c> 只把 <c>x</c> 限定到 <c>a</c>；要限定整体必须写
/// <c>{a} : ("x" AND "y")</c>。</para>
/// </summary>
internal static class FtsExpression
{
    /// <summary>解析表达式；语法错误抛 <see cref="FormatException"/>。</summary>
    public static FtsNode Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var parser = new Parser(expression);
        var node = parser.ParseOr();
        parser.ExpectEnd();
        return node;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text) => _text = text;

        public FtsNode ParseOr()
        {
            var children = new List<FtsNode> { ParseAnd() };
            while (TryKeyword("OR")) children.Add(ParseAnd());
            return children.Count == 1 ? children[0] : new FtsOr(children);
        }

        private FtsNode ParseAnd()
        {
            var children = new List<FtsNode> { ParseUnary() };
            while (TryKeyword("AND")) children.Add(ParseUnary());
            return children.Count == 1 ? children[0] : new FtsAnd(children);
        }

        /// <summary>列作用域绑定紧随其后的一个 primary / 嵌套作用域。</summary>
        private FtsNode ParseUnary()
        {
            SkipWhitespace();
            if (Peek() != '{') return ParsePrimary();

            var columns = ParseColumns();
            SkipWhitespace();
            Expect(':');
            return new FtsScope(columns, ParseUnary());
        }

        private FtsNode ParsePrimary()
        {
            SkipWhitespace();
            var c = Peek();
            if (c == '(')
            {
                _pos++;
                var node = ParseOr();
                SkipWhitespace();
                Expect(')');
                return node;
            }
            if (c == '"') return ParseQuoted();
            if (_pos >= _text.Length) throw new FormatException("表达式意外结束");

            // 编译器总是给词元加引号；这里仅做容错解析（裸词 + 可选前缀）。
            var start = _pos;
            while (_pos < _text.Length && !IsDelimiter(_text[_pos])) _pos++;
            if (_pos == start) throw new FormatException($"无法解析字符 '{_text[_pos]}'（位置 {_pos}）");
            var token = _text[start.._pos];
            var prefix = _pos < _text.Length && _text[_pos] == '*';
            if (prefix) _pos++;
            return new FtsTerm(token, prefix);
        }

        private FtsNode ParseQuoted()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw new FormatException("引号未闭合");
                var c = _text[_pos++];
                if (c != '"')
                {
                    builder.Append(c);
                    continue;
                }
                // FTS5 短语内用 "" 表示一个字面双引号。
                if (_pos < _text.Length && _text[_pos] == '"')
                {
                    builder.Append('"');
                    _pos++;
                    continue;
                }
                break;
            }

            var prefix = _pos < _text.Length && _text[_pos] == '*';
            if (prefix) _pos++;

            var tokens = builder.ToString().Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) throw new FormatException("空短语");
            return tokens.Length == 1
                ? new FtsTerm(tokens[0], prefix)
                : new FtsPhrase(tokens, prefix);
        }

        private List<string> ParseColumns()
        {
            Expect('{');
            var columns = new List<string>();
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    break;
                }
                if (_pos >= _text.Length) throw new FormatException("列作用域未闭合");
                var start = _pos;
                while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos]) && _text[_pos] != '}')
                    _pos++;
                if (_pos == start) throw new FormatException($"列作用域解析失败（位置 {_pos}）");
                columns.Add(_text[start.._pos]);
            }
            if (columns.Count == 0) throw new FormatException("空列作用域");
            return columns;
        }

        /// <summary>尝试读取关键字（前后需为非标识符边界）；不匹配时位置不变。</summary>
        private bool TryKeyword(string keyword)
        {
            SkipWhitespace();
            if (_pos + keyword.Length > _text.Length) return false;
            if (string.Compare(_text, _pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            var end = _pos + keyword.Length;
            if (end < _text.Length && !IsDelimiter(_text[end])) return false;
            _pos = end;
            return true;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos != _text.Length)
                throw new FormatException($"表达式尾部有多余内容（位置 {_pos}）");
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (_pos >= _text.Length || _text[_pos] != expected)
                throw new FormatException(
                    $"期望 '{expected}'，实际 {(_pos >= _text.Length ? "结束" : $"'{_text[_pos]}'")}（位置 {_pos}）");
            _pos++;
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private static bool IsDelimiter(char c) =>
            char.IsWhiteSpace(c) || c is '(' or ')' or '{' or '}' or ':' or '"';
    }
}
