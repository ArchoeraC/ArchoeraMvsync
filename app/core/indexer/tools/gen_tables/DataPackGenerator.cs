// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 数据包 manifest 与 Dart 同源常量的写入（T4.3 §5.5.1）。
//
// 关键约束：id 的规范化与运行时**共用同一条代码路径**（DataPackId，Link 自
// ../../DataPack.cs），所以生成器和 NativeAOT 库算出的 data_pack_id 必然一致。
// manifest 本身只负责「可读 + 可 diff」：缩进 2 空格、LF、files 按 name 排序、
// 无尾随空白 / 无尾随换行；id 由内容重算，与文件排版无关。

using System.Text;
using System.Text.Json;

namespace Archoera.Indexer.Tools;

/// <summary>写 <c>data/manifest.json</c> 与 <c>app/lib/services/search/data_pack.g.dart</c>。</summary>
internal static class DataPackGenerator
{
    /// <summary>写 manifest（UTF-8 无 BOM、LF、无尾随换行）。</summary>
    public static void WriteManifest(string output, DataPackManifest manifest) =>
        File.WriteAllBytes(output, RenderManifest(manifest));

    /// <summary>manifest 的确定性字节（<c>--check</c> 与写入共用，防止两套排版漂移）。</summary>
    public static byte[] RenderManifest(DataPackManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", manifest.Schema);
            writer.WriteString("name", manifest.Name);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("unicode", manifest.Unicode);
            writer.WriteString("generator", manifest.Generator);
            writer.WriteStartArray("files");
            foreach (var file in manifest.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("bytes", file.Bytes);
                writer.WriteString("license", file.License);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 写 Dart 常量（与原生 health 的 <c>dataPack.id</c> 比对）。
    /// 生成命令写在文件头，方便审计「这个常量从哪来」。
    /// </summary>
    public static void WriteDartConstant(string output, string dataPackId) =>
        File.WriteAllText(output, RenderDartConstant(dataPackId),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>Dart 常量的确定性文本（<c>--check</c> 与写入共用）。</summary>
    public static string RenderDartConstant(string dataPackId)
    {
        return new StringBuilder()
            .AppendLine("// ArchoeraMvsync UI")
            .AppendLine("// Copyright (C) 2026 Archoera && BetaStudio2")
            .AppendLine("// SPDX-License-Identifier: AGPL-3.0-or-later")
            .AppendLine("//")
            .AppendLine("// 生成文件，勿手改（T4.3 §5.5.3 同源校验）。")
            .AppendLine("// 生成命令（在 app/core/indexer 下）：")
            .AppendLine("//   dotnet run --project tools/gen_tables -- --manifest-only")
            .AppendLine("// 来源：app/core/indexer/data/manifest.json")
            .AppendLine("//   data_pack_id = 'sha256:' + sha256(规范化 manifest，去掉 id)（规则见 DataPack.cs）")
            .AppendLine("// 用途：与 era_search_health 的 dataPack.id 比对；不一致 → 设置页提示重建索引 / 重装。")
            .AppendLine()
            .AppendLine("/// 搜索数据包 id（编译期常量，必须与索引器内嵌的数据包一致）。")
            .Append("const String dataPackId = '")
            .Append(dataPackId)
            .AppendLine("';")
            .ToString();
    }
}
