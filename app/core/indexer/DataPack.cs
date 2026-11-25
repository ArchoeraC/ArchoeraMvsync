// ArchoeraMvsync
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Archoera.Indexer;

/// <summary>数据包清单中的一个文件（<c>files[]</c> 的元素）。</summary>
internal sealed class DataPackFile
{
    public string Name { get; init; } = "";

    /// <summary>文件内容 SHA-256（64 位小写十六进制，不带前缀）。</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>文件字节数。</summary>
    public long Bytes { get; init; }

    /// <summary>许可证标识（Unicode / AGPL-3.0-or-later / …，见 §5.5.5）。</summary>
    public string License { get; init; } = "";
}

/// <summary>
/// <c>data/manifest.json</c> 的内存模型（§5.5.1）。
///
/// <para>**id 不在 manifest 里**：<see cref="DataPackId"/> 由规范化后的 manifest 现算，
/// 写入索引 Manifest 的 <c>anchors.dataPackId</c>。因此 manifest 文件的排版（缩进、
/// 文件顺序）不影响 id，只有内容影响。</para>
/// </summary>
internal sealed class DataPackManifest
{
    public int Schema { get; set; } = DataPack.SchemaVersion;

    public string Name { get; set; } = DataPack.PackName;

    public string Version { get; set; } = "1.0.0";

    /// <summary>数据来源的 Unicode Character Database 版本（见 data/README.md 的口径）。</summary>
    public string Unicode { get; set; } = "";

    public string Generator { get; set; } = "";

    public List<DataPackFile> Files { get; } = new();

    /// <summary>
    /// 解析 manifest JSON。只接受已知 <c>schema</c>；未知顶层键（含历史 <c>id</c>）忽略，
    /// 因为它们不参与 <see cref="DataPackId"/> 的规范化。
    /// </summary>
    public static DataPackManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0 || json.Length > MaxManifestBytes)
            throw new InvalidDataException($"数据包 manifest 长度超出 1..{MaxManifestBytes} 字节");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("数据包 manifest 不是合法 JSON", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("数据包 manifest 根不是对象");

            var schema = RequireInt(root, "schema");
            if (schema != DataPack.SchemaVersion)
                throw new InvalidDataException($"数据包 manifest schema 不受支持: {schema}");

            var manifest = new DataPackManifest
            {
                Schema = schema,
                Name = RequireString(root, "name"),
                Version = RequireString(root, "version"),
                Unicode = RequireString(root, "unicode"),
                Generator = RequireString(root, "generator"),
            };
            if (manifest.Name.Length == 0) throw new InvalidDataException("数据包 manifest name 为空");
            if (manifest.Version.Length == 0) throw new InvalidDataException("数据包 manifest version 为空");

            var files = root.TryGetProperty("files", out var filesElement)
                ? filesElement
                : throw new InvalidDataException("数据包 manifest 缺少 files");
            if (files.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("数据包 manifest files 不是数组");
            if (files.GetArrayLength() == 0)
                throw new InvalidDataException("数据包 manifest files 为空");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("数据包 manifest file 不是对象");

                var name = RequireString(item, "name");
                if (!DataPack.IsSafeFileName(name))
                    throw new InvalidDataException($"数据包 manifest 文件名非法: {name}");
                if (!seen.Add(name))
                    throw new InvalidDataException($"数据包 manifest 文件重复: {name}");

                var sha = RequireString(item, "sha256");
                if (sha.Length != 64 || !IsLowerHex(sha))
                    throw new InvalidDataException($"数据包 manifest sha256 非法: {name}");

                var bytes = RequireLong(item, "bytes");
                if (bytes < 0)
                    throw new InvalidDataException($"数据包 manifest bytes 为负: {name}");

                var license = RequireString(item, "license");
                if (license.Length == 0)
                    throw new InvalidDataException($"数据包 manifest license 为空: {name}");

                manifest.Files.Add(new DataPackFile
                {
                    Name = name,
                    Sha256 = sha,
                    Bytes = bytes,
                    License = license,
                });
            }

            return manifest;
        }
    }

    /// <summary>memory manifest 上限（与 EraStore Manifest 同量级；实际约 1.5 KB）。</summary>
    private const int MaxManifestBytes = 256 * 1024;

    private static JsonElement Require(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element)
            ? element
            : throw new InvalidDataException($"数据包 manifest 缺少字段 {name}");

    private static string RequireString(JsonElement parent, string name) =>
        Require(parent, name) is { ValueKind: JsonValueKind.String } element
        && element.GetString() is { } value
            ? value
            : throw new InvalidDataException($"数据包 manifest 字段 {name} 非法");

    private static int RequireInt(JsonElement parent, string name) =>
        Require(parent, name).TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"数据包 manifest 字段 {name} 非法");

    private static long RequireLong(JsonElement parent, string name) =>
        Require(parent, name).TryGetInt64(out var value)
            ? value
            : throw new InvalidDataException($"数据包 manifest 字段 {name} 非法");

    private static bool IsLowerHex(string text)
    {
        foreach (var ch in text)
        {
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}

/// <summary>
/// <c>data_pack_id</c> 的规范化与计算（§5.5.1）。**生成器与运行时共用本类**，保证
/// 两边算出的 id 逐字节一致（生成器通过 csproj Link 编译同一文件）。
///
/// <para>规范化规则（唯一事实来源）：</para>
/// <list type="number">
///   <item>对象键顺序固定：<c>schema</c>、<c>name</c>、<c>version</c>、<c>unicode</c>、<c>generator</c>、<c>files</c>；</item>
///   <item><c>files</c> 按 <c>name</c> 的 UTF-8 字节序（序数比较）升序；</item>
///   <item>每个文件对象的键顺序固定：<c>name</c>、<c>sha256</c>、<c>bytes</c>、<c>license</c>；</item>
///   <item>紧凑 JSON（无缩进 / 无空格）、UTF-8 无 BOM、LF、无尾随空白 / 无尾随换行；</item>
///   <item>未知字段与 <c>id</c> 字段一律不参与规范化（因此「去掉 id」即天然成立）。</item>
/// </list>
/// <para><c>data_pack_id = "sha256:" + lowerhex(sha256(canonical))</c>。</para>
/// </summary>
internal static class DataPackId
{
    public static byte[] Canonicalize(DataPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
        return buffer.WrittenSpan.ToArray();
    }

    public static string Compute(DataPackManifest manifest) =>
        "sha256:" + Sha256Hex(Canonicalize(manifest));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>流式 SHA-256（大文件按 256 KB 增量读取，避免整表常驻）。</summary>
    public static string Sha256Hex(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>数据包文件来源（内嵌资源 / 磁盘目录 / 测试内存源）。</summary>
internal interface IDataPackSource
{
    bool Exists(string name);

    long? Length(string name);

    Stream Open(string name);
}

/// <summary>程序集内嵌资源（生产形态：NativeAOT 单文件、无运行期文件系统依赖）。</summary>
internal sealed class EmbeddedDataPackSource : IDataPackSource
{
    private readonly Assembly _assembly = typeof(DataPack).Assembly;

    private string ResourceName(string name) => DataPack.ResourcePrefix + name;

    public bool Exists(string name) =>
        _assembly.GetManifestResourceInfo(ResourceName(name)) is not null;

    public long? Length(string name)
    {
        using var stream = Open(name);
        return stream.Length;
    }

    public Stream Open(string name) =>
        _assembly.GetManifestResourceStream(ResourceName(name))
        ?? throw new FileNotFoundException($"内嵌数据包文件缺失: {name}", name);
}

/// <summary>磁盘目录来源：<c>verify --data</c>、测试与生成期校验使用。</summary>
internal sealed class DirectoryDataPackSource : IDataPackSource
{
    private readonly string _directory;

    public DirectoryDataPackSource(string directory) => _directory = directory;

    private string PathOf(string name)
    {
        if (!DataPack.IsSafeFileName(name))
            throw new InvalidDataException($"数据包文件名非法: {name}");
        return Path.Combine(_directory, name);
    }

    public bool Exists(string name) => File.Exists(PathOf(name));

    public long? Length(string name) =>
        File.Exists(PathOf(name)) ? new FileInfo(PathOf(name)).Length : null;

    public Stream Open(string name) => File.OpenRead(PathOf(name));
}

/// <summary>全量校验结果（<c>verify --data</c> / 设置页「校验数据」）。</summary>
internal sealed class DataPackVerification
{
    public bool Ok;

    /// <summary>被校验数据包（磁盘副本）的 id；manifest 不可读时为空。</summary>
    public string Id = "";

    /// <summary>当前编译进索引器的数据包 id（同一 manifest 应相等）。</summary>
    public string EmbeddedId = "";

    /// <summary>磁盘 id 与编译期 id 是否一致（防止「改了 tsv 没重新生成 manifest」）。</summary>
    public bool Match;

    public bool Present;

    public List<string> Missing = new();
    public List<string> Corrupt = new();
    public List<string> Degraded = new();
    public List<string> Errors = new();
}

/// <summary>
/// 搜索数据包（<c>app/core/indexer/data/</c>）：manifest 解析、id 计算、按需全量校验、
/// 缺失 / 损坏文件的能力降级（§5.5.1–§5.5.6）。
///
/// <para>**为什么它是索引的一部分**：包一变，词元 / 读音 / 归一化就可能变，索引必须重建。
/// 构建期把 <see cref="Id"/> 写进 EraStore Manifest，运行期只比对这个字符串；不一致 →
/// 索引作废并全量重建，**绝不**读旧索引配新表。</para>
///
/// <para>**启动期只做便宜检查**（文件存在 + 字节数）；逐文件 sha256 只在
/// <see cref="VerifyAll"/>（<c>verify --data</c> / 设置页「校验数据」）执行。</para>
///
/// <para>**降级矩阵**（§5.5.6）：</para>
/// <list type="bullet">
///   <item><c>nfkc</c> / <c>accent_fold</c> / <c>variant_fold</c>：基础表，缺失即
///         <see cref="FoundationOk"/>=false，构建 / 查询拒绝并给出可操作提示；</item>
///   <item><c>def_en2hanzi</c> / <c>def_hanzi2en</c>：去掉 <c>cross_lingual</c>；</item>
///   <item><c>ja_readings</c> / <c>kana_romaji</c>：去掉 <c>readings</c>（韩文读音不依赖数据包）。</item>
/// </list>
/// </summary>
internal sealed class DataPack
{
    public const int SchemaVersion = 1;
    public const string PackName = "archoera-search-data";
    public const string GeneratorId = "gen_tables@1";
    public const string ManifestFileName = "manifest.json";
    public const string ResourcePrefix = "Archoera.Indexer.data.";

    public const string NfkcFile = "nfkc.tsv";
    public const string AccentFile = "accent_fold.tsv";
    public const string VariantFile = "variant_fold.tsv";
    public const string KanaRomajiFile = "kana_romaji.tsv";
    public const string JaReadingsFile = "ja_readings.tsv";
    public const string LexiconFile = "lexicon_cross.tsv";
    public const string GlossEnToHanziFile = "def_en2hanzi.tsv";
    public const string GlossHanziToEnFile = "def_hanzi2en.tsv";

    /// <summary>能力名（与 <see cref="IndexCapabilities"/> 同值；此文件不依赖那套源码以便生成器复用）。</summary>
    public const string CapabilityCrossLingual = "cross_lingual";
    public const string CapabilityReadings = "readings";

    /// <summary>基础归一化表：缺失 → 拒绝构建 / 查询（不允许静默降级正确性）。</summary>
    private static readonly string[] FoundationFiles =
    {
        NfkcFile, AccentFile, VariantFile,
    };

    /// <summary>文件 → 其缺失时受影响的能力（基础表不在此表，由 <see cref="FoundationOk"/> 表达）。</summary>
    private static readonly Dictionary<string, string> CapabilityFiles = new(StringComparer.Ordinal)
    {
        [GlossEnToHanziFile] = CapabilityCrossLingual,
        [GlossHanziToEnFile] = CapabilityCrossLingual,
        [LexiconFile] = CapabilityCrossLingual,
        [JaReadingsFile] = CapabilityReadings,
        [KanaRomajiFile] = CapabilityReadings,
    };

    /// <summary>数据包声明的全部「已知」文件（manifest 未声明也按缺失处理，避免静默少召回）。</summary>
    private static readonly string[] KnownFiles =
    {
        NfkcFile, AccentFile, VariantFile, KanaRomajiFile, JaReadingsFile,
        GlossEnToHanziFile, GlossHanziToEnFile, LexiconFile,
    };

    private static readonly Lazy<DataPack> EmbeddedPack =
        new(static () => Load(new EmbeddedDataPackSource()));

    /// <summary>编译进当前程序集的数据包（生产唯一形态）。</summary>
    public static DataPack Embedded => EmbeddedPack.Value;

    /// <summary>
    /// 测试接缝：非空时 <see cref="Current"/> 返回它（模拟缺失 / 损坏 / 换包）。
    /// **生产代码不得写入**；测试须在 finally 里置回 null。
    /// </summary>
    internal static DataPack? TestOverride { get; set; }

    /// <summary>当前生效的数据包（生产 = <see cref="Embedded"/>）。</summary>
    public static DataPack Current => TestOverride ?? EmbeddedPack.Value;

    private readonly IDataPackSource _source;

    private DataPack(IDataPackSource source) => _source = source;

    public DataPackManifest Manifest { get; private set; } = new();

    /// <summary>规范化 manifest 的 id；manifest 不可读时为空串。</summary>
    public string Id { get; private set; } = "";

    /// <summary>manifest 存在且 schema / 字段合法。</summary>
    public bool Present { get; private set; }

    /// <summary>基础归一化表齐全（否则拒绝构建 / 查询）。</summary>
    public bool FoundationOk { get; private set; }

    /// <summary>manifest 本身的错误（不可读 / 非法 schema）；正常为空。</summary>
    public string? Error { get; private set; }

    public List<string> MissingFiles { get; } = new();

    /// <summary>大小不符（启动期）或 sha256 不符（<see cref="VerifyAll"/> 后）的文件。</summary>
    public List<string> CorruptFiles { get; } = new();

    /// <summary>因文件缺失 / 损坏而关闭的能力（<c>cross_lingual</c> / <c>readings</c>）。</summary>
    public List<string> DegradedCapabilities { get; } = new();

    /// <summary>人可读、可操作的拒绝原因（基础表缺失 / manifest 不可读时非空）。</summary>
    public string FoundationError
    {
        get
        {
            if (Present && FoundationOk) return "";
            if (!Present)
            {
                return "内置搜索数据包缺失（data/manifest.json）：无法保证归一化 / 读音表正确性，"
                    + "已停用索引（搜索回退基础匹配）。请重装应用恢复内置数据包。";
            }
            var missing = FoundationFiles
                .Where(name => !IsUsable(name))
                .Select(name => $"{name}（缺失或损坏）")
                .ToArray();
            return $"内置搜索数据包基础表不可用：{string.Join("、", missing)}。"
                + "这些是对所有查询生效的归一化表，不允许缺；已拒绝构建 / 查询索引。"
                + "请重装应用恢复内置数据包（app/core/indexer/data）。";
        }
    }

    /// <summary>从磁盘目录加载（<c>verify --data</c> / 测试 / 生成期）。</summary>
    public static DataPack FromDirectory(string directory) =>
        Load(new DirectoryDataPackSource(directory));

    /// <summary>从任意来源加载；绝不抛异常（错误经 <see cref="Error"/> / 列表暴露）。</summary>
    public static DataPack Load(IDataPackSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var pack = new DataPack(source);
        try
        {
            if (!source.Exists(ManifestFileName))
            {
                pack.Error = "数据包 manifest 缺失";
                pack.RecalculateCapabilities();
                return pack;
            }

            using var stream = source.Open(ManifestFileName);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            pack.Manifest = DataPackManifest.Parse(memory.ToArray());
            pack.Present = true;
            pack.Id = DataPackId.Compute(pack.Manifest);
            pack.CheckDeclaredFiles();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                       or UnauthorizedAccessException or FormatException)
        {
            pack.Present = false;
            pack.Id = "";
            pack.Error = "数据包 manifest 不可读: " + ex.Message;
        }
        pack.RecalculateCapabilities();
        return pack;
    }

    /// <summary>文件是否可用（manifest 已声明、存在、且启动期大小检查通过）。</summary>
    public bool IsUsable(string name)
    {
        if (!Present) return false;
        if (!Manifest.Files.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal)))
            return false;
        return !MissingFiles.Contains(name) && !CorruptFiles.Contains(name);
    }

    /// <summary>打开数据包文件（不存在 / 未声明返回 false，调用方按空表降级）。</summary>
    public bool TryOpen(string name, out Stream stream)
    {
        stream = Stream.Null;
        if (!Present) return false;
        var declared = Manifest.Files.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal));
        if (!declared || MissingFiles.Contains(name) || CorruptFiles.Contains(name)) return false;
        try
        {
            stream = _source.Open(name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 全量校验：逐文件 sha256（+ 可选 TSV 结构检查）。只在 <c>verify --data</c> /
    /// 设置页「校验数据」调用；结果会并把不符文件并入 <see cref="CorruptFiles"/>。
    /// </summary>
    public DataPackVerification VerifyAll(bool validateTables = false)
    {
        var report = new DataPackVerification
        {
            Present = Present,
            Id = Id,
            EmbeddedId = Embedded.Id,
            Match = Present && Id.Length > 0 && string.Equals(Id, Embedded.Id, StringComparison.Ordinal),
        };
        report.Missing.AddRange(MissingFiles);
        report.Corrupt.AddRange(CorruptFiles);
        report.Degraded.AddRange(DegradedCapabilities);
        if (!Present)
        {
            report.Errors.Add(Error ?? "数据包不可读");
            report.Ok = false;
            return report;
        }

        foreach (var file in Manifest.Files)
        {
            if (MissingFiles.Contains(file.Name)) continue;
            try
            {
                using var stream = _source.Open(file.Name);
                var actual = DataPackId.Sha256Hex(stream);
                if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
                {
                    if (!report.Corrupt.Contains(file.Name)) report.Corrupt.Add(file.Name);
                    if (!CorruptFiles.Contains(file.Name)) CorruptFiles.Add(file.Name);
                    report.Errors.Add($"sha256 不符: {file.Name}（期望 {file.Sha256}，实际 {actual}）");
                    continue;
                }
                if (validateTables && DataPackTableValidator.IsTableFile(file.Name))
                {
                    using var table = _source.Open(file.Name);
                    using var memory = new MemoryStream();
                    table.CopyTo(memory);
                    foreach (var error in DataPackTableValidator.Validate(file.Name, memory.ToArray()))
                        report.Errors.Add($"{file.Name}: {error}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!report.Corrupt.Contains(file.Name)) report.Corrupt.Add(file.Name);
                report.Errors.Add($"{file.Name}: 读取失败（{ex.Message}）");
            }
        }

        foreach (var name in MissingFiles)
            report.Errors.Add($"文件缺失: {name}");
        if (!report.Match)
        {
            report.Errors.Add(
                $"磁盘数据包 id 与索引器内嵌数据包不一致（磁盘 {Id} / 内嵌 {Embedded.Id}）："
                + "请重新运行 manifest 生成并重建索引");
        }
        report.Degraded.Clear();
        report.Degraded.AddRange(DegradedCapabilities);
        report.Ok = report.Errors.Count == 0
            && report.Corrupt.Count == 0
            && report.Missing.Count == 0
            && FoundationOk;
        return report;
    }

    /// <summary>构建 / 查询入口的硬门禁：基础表缺失抛可操作错误（<c>InvalidDataException</c>）。</summary>
    public void ThrowIfFoundationMissing()
    {
        if (Present && FoundationOk) return;
        throw new InvalidDataException(FoundationError);
    }

    /// <summary>把数据包降级信息序列化为健康页 / explain 用的结构。</summary>
    public DataPackHealth Health(string storedId) => new()
    {
        Id = Id,
        StoredId = storedId,
        Present = Present,
        FoundationOk = FoundationOk,
        Missing = MissingFiles.ToArray(),
        Corrupt = CorruptFiles.ToArray(),
        Degraded = DegradedCapabilities.ToArray(),
    };

    /// <summary>启动期便宜检查：manifest 声明的文件必须存在且字节数一致。</summary>
    private void CheckDeclaredFiles()
    {
        foreach (var file in Manifest.Files)
        {
            if (!_source.Exists(file.Name))
            {
                MissingFiles.Add(file.Name);
                continue;
            }
            var length = _source.Length(file.Name);
            if (length is { } actual && actual != file.Bytes)
                CorruptFiles.Add(file.Name);
        }
        // manifest 未声明的已知文件按缺失处理：宁可少声明能力，也不静默少召回。
        foreach (var known in KnownFiles)
        {
            if (!Manifest.Files.Any(file => string.Equals(file.Name, known, StringComparison.Ordinal)))
                MissingFiles.Add(known);
        }
    }

    private void RecalculateCapabilities()
    {
        DegradedCapabilities.Clear();
        foreach (var (file, capability) in CapabilityFiles)
        {
            if (IsUsable(file)) continue;
            if (!DegradedCapabilities.Contains(capability)) DegradedCapabilities.Add(capability);
        }
        DegradedCapabilities.Sort(StringComparer.Ordinal);
        FoundationOk = Present && FoundationFiles.All(IsUsable);
    }

    /// <summary>文件名安全：无路径分隔符、无 <c>..</c>、无控制字符（防目录穿越）。</summary>
    internal static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) return false;
        if (name is "." or "..") return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        foreach (var ch in name)
        {
            if (ch < 0x20 || ch == 0x7F) return false;
        }
        return true;
    }

    /// <summary>
    /// 从磁盘目录构建 manifest（生成器用）：枚举 <c>*.tsv</c>，按名排序并计算
    /// sha256 / 字节数；许可证由调用方按文件名给出（不在运行时数据里「猜」）。
    /// </summary>
    public static DataPackManifest BuildManifestFromDirectory(
        string directory, IReadOnlyDictionary<string, string> licenses,
        string unicode, string generator = GeneratorId, string version = "1.0.0")
    {
        var manifest = new DataPackManifest
        {
            Schema = SchemaVersion,
            Name = PackName,
            Version = version,
            Unicode = unicode,
            Generator = generator,
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*.tsv").OrderBy(
                     p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (!IsSafeFileName(name))
                throw new InvalidDataException($"数据包文件名非法: {name}");
            if (!licenses.TryGetValue(name, out var license) || string.IsNullOrWhiteSpace(license))
                throw new InvalidDataException(
                    $"生成器缺少 {name} 的许可证登记（在 tools/gen_tables 的 LICENSE_BY_FILE 中补充）");
            var bytes = File.ReadAllBytes(path);
            manifest.Files.Add(new DataPackFile
            {
                Name = name,
                Sha256 = DataPackId.Sha256Hex(bytes),
                Bytes = bytes.LongLength,
                License = license,
            });
        }
        if (manifest.Files.Count == 0)
            throw new InvalidDataException($"数据包目录没有 *.tsv: {directory}");
        return manifest;
    }

    /// <summary>定位仓库内数据包目录（cwd / 输出目录向上找 <c>data/manifest.json</c>）。</summary>
    public static string? FindDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "data"),
            Path.Combine(AppContext.BaseDirectory, "data"),
        };
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "data"));
        current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "data"));

        foreach (var candidate in candidates)
            if (File.Exists(Path.Combine(candidate, ManifestFileName)))
                return Path.GetFullPath(candidate);
        return null;
    }
}

/// <summary>健康页 / explain 用的数据包诊断（additive JSON 字段）。</summary>
internal sealed class DataPackHealth
{
    /// <summary>编译进当前索引器的数据包 id。</summary>
    public string Id = "";

    /// <summary>索引 Manifest 里记录的 id（旧索引可能为空）。</summary>
    public string StoredId = "";

    public bool Present;
    public bool FoundationOk;
    public IReadOnlyList<string> Missing = Array.Empty<string>();
    public IReadOnlyList<string> Corrupt = Array.Empty<string>();
    public IReadOnlyList<string> Degraded = Array.Empty<string>();

    public bool Match => Id.Length > 0 && string.Equals(Id, StoredId, StringComparison.Ordinal);
}

/// <summary>
/// 数据包 TSV 结构校验（§5.5.3 第 3 条）：UTF-8、两列、键非空且不重复、单行 ≤4 KB、
/// 无控制字符。只在生成 / <c>verify --data</c> 执行，运行期不扫表。
/// </summary>
internal static class DataPackTableValidator
{
    public const int MaxLineBytes = 4096;
    public const int MaxLines = 2_000_000;

    public static bool IsTableFile(string name) =>
        name.EndsWith(".tsv", StringComparison.Ordinal);

    public static List<string> Validate(string name, ReadOnlySpan<byte> bytes)
    {
        var errors = new List<string>();
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            errors.Add("不是合法 UTF-8");
            return errors;
        }

        if (text.Length > 0 && text[0] == '\uFEFF') errors.Add("含 UTF-8 BOM");

        // 策展词表是 6 列（source/target/weight/source/license/note），与其它 KEY<TAB>VALUE
        // 表不同；这里只校验结构，语义校验（单字源 / 权重 / 许可证）由 LexiconCross 负责。
        if (name == DataPack.LexiconFile) return ValidateLexicon(text, errors);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var raw in text.Split('\n'))
        {
            lines++;
            if (lines > MaxLines)
            {
                errors.Add($"行数超过 {MaxLines}");
                break;
            }
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0 || line[0] == '#') continue;
            if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
            {
                errors.Add($"第 {lines} 行超过 {MaxLineBytes} 字节");
                continue;
            }
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
            {
                errors.Add($"第 {lines} 行不是 KEY<TAB>VALUE");
                continue;
            }
            if (line.IndexOf('\t', tab + 1) >= 0)
            {
                errors.Add($"第 {lines} 行列数不是 2");
                continue;
            }
            var key = line[..tab];
            if (!keys.Add(key))
            {
                errors.Add($"重复键: {key}");
                continue;
            }
            foreach (var ch in line)
            {
                if ((ch < 0x20 && ch != '\t') || ch == 0x7F)
                {
                    errors.Add($"第 {lines} 行含控制字符");
                    break;
                }
            }
        }
        return errors;
    }

    private static List<string> ValidateLexicon(string text, List<string> errors)
    {
        var lines = 0;
        foreach (var raw in text.Split('\n'))
        {
            lines++;
            if (lines > MaxLines) { errors.Add($"行数超过 {MaxLines}"); break; }
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0 || line[0] == '#') continue;
            if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
            {
                errors.Add($"第 {lines} 行超过 {MaxLineBytes} 字节");
                continue;
            }
            var tabs = 0;
            foreach (var ch in line)
            {
                if (ch == '\t') tabs++;
                else if ((ch < 0x20) || ch == 0x7F)
                {
                    errors.Add($"第 {lines} 行含控制字符");
                    break;
                }
            }
            if (tabs != 5) errors.Add($"第 {lines} 行应为 6 列（实际 {tabs + 1} 列）");
        }
        return errors;
    }
}
