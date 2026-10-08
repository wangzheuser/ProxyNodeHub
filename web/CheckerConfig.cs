using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ProxyNodeHub.Web;

// YAML parsing identifies real top-level values. Only their source spans are
// replaced: the rest of the detector's file, including secrets, stays verbatim.
public sealed class CheckerConfig
{
    public sealed record Field(string Key, string Label, string Kind, double? Min = null,
        double? Max = null, string[]? Choices = null);

    public static IReadOnlyList<Field> Fields { get; } = new Field[]
    {
        new("concurrent", "测活并发", "integer", 1, 200),
        new("speed-concurrent", "测速并发", "integer", 1, 100),
        new("media-concurrent", "媒体检测并发", "integer", 1, 200),
        new("check-interval", "检测间隔（分钟；未配置 cron 时生效）", "integer", 1, 1440),
        new("timeout", "测活超时（毫秒）", "integer", 500, 30000),
        new("min-speed", "最低速度（KB/s）", "integer", 0, 100000),
        new("download-timeout", "测速时长（秒）", "integer", 1, 60),
        new("download-mb", "单节点下载上限（MB）", "integer", 0, 500),
        new("total-speed-limit", "总下载限速（MB/s，0不限）", "integer", 0, 1000),
        new("alive-test-url", "测活地址", "url"),
        new("speed-test-url", "测速地址（空为关闭测速）", "url"),
        new("node-type", "协议筛选", "list"),
        new("node-prefix", "节点名前缀", "string"),
        new("media-check", "启用媒体检测", "boolean"),
        new("platforms", "媒体检测平台", "list"),
        new("rename-node", "查询节点归属地", "boolean"),
        new("keep-days", "历史节点保留天数（0关闭）", "integer", 0, 365),
        new("save-method", "保存方式（凭据在原管理台配置）", "choice", Choices: ["local", "gist", "r2", "webdav", "s3"]),
        new("output-dir", "输出目录（更改后重启以同步订阅路由）", "string"),
        new("github-proxy", "订阅下载镜像", "url"),
        new("success-rate", "订阅成功率告警阈值（0–1）", "number", 0, 1),
        new("sub-urls-retry", "订阅下载重试次数", "integer", 0, 20),
        new("sub-urls-timeout", "订阅下载超时（秒，不是节点测活超时）", "integer", 1, 120),
        new("sub-urls-concurrent", "订阅下载并发", "integer", 1, 100),
        new("sub-store-port", "Sub-Store 端口（重启生效，空为关闭）", "string"),
        new("ipv6", "允许 IPv6", "boolean"),
        new("shuffle-test-order", "打散测试顺序", "boolean"),
        new("success-limit", "最大成功节点数（0不限）", "integer", 0, 1000000),
    };

    private sealed record Entry(int Start, int End, bool Linked);
    private readonly string yaml;
    private readonly Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int insertAt;

    public CheckerConfig(string content)
    {
        if (content.Length > 1024 * 1024) throw new InvalidDataException("检测器配置超过1 MiB，请使用原管理台。");
        yaml = content;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root ||
                root.Style != MappingStyle.Block)
                throw new InvalidDataException("安全参数编辑仅支持单个顶层 YAML 映射，请使用原管理台编辑其他形式。");
            foreach (var pair in root.Children)
            {
                if (pair.Key is not YamlScalarNode { Value: { } key } || key == "<<" || !values.TryAdd(key, pair.Value))
                    throw new InvalidDataException("配置存在重复键、复杂键或顶层合并键，请使用原管理台处理。");
            }

            var parser = new Parser(new StringReader(yaml));
            var events = new List<ParsingEvent>();
            while (parser.MoveNext()) if (parser.Current is { } item) events.Add(item);
            var index = events.FindIndex(e => e is MappingStart) + 1;
            while (index > 0 && events[index] is not MappingEnd)
            {
                if (events[index++] is not Scalar key) throw new InvalidDataException("检测器顶层键必须为字符串。");
                var start = checked((int)key.Start.Index);
                var end = checked((int)key.End.Index);
                var depth = 0;
                var linked = false;
                var closers = new Stack<char>();
                do
                {
                    var item = events[index++];
                    linked |= item is AnchorAlias || item is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty);
                    if (item is MappingStart or SequenceStart)
                    {
                        depth++;
                        var at = checked((int)item.Start.Index);
                        closers.Push(item is SequenceStart { Style: SequenceStyle.Flow } && at < yaml.Length && yaml[at] == '[' ? ']'
                            : item is MappingStart { Style: MappingStyle.Flow } && at < yaml.Length && yaml[at] == '{' ? '}' : '\0');
                    }
                    if (item is MappingEnd or SequenceEnd)
                    {
                        depth--;
                        var closer = closers.Pop();
                        if (closer != '\0')
                        {
                            // YamlDotNet flow-end marks point AT the closing
                            // delimiter, not beyond it (unlike scalar ends).
                            var at = checked((int)item.Start.Index);
                            if (at >= yaml.Length || yaml[at] != closer)
                                throw new InvalidDataException("无法定位 YAML 集合边界，请使用原管理台。");
                            end = Math.Max(end, at + 1);
                        }
                    }
                    else
                        end = Math.Max(end, checked((int)item.End.Index));
                } while (depth > 0);
                while (end > start && char.IsWhiteSpace(yaml[end - 1])) end--;
                entries.Add(key.Value, new(start, end, linked));
            }
            insertAt = checked((int)events[index].Start.Index);
        }
        catch (YamlException)
        {
            // Parser messages can quote raw configuration, including secrets.
            throw new InvalidDataException("检测器 YAML 无法安全解析，请在原管理台检查语法。");
        }
    }

    public object Project(string revision)
    {
        var fields = Fields.Select(field =>
        {
            var linked = entries.TryGetValue(field.Key, out var entry) && entry.Linked;
            var value = linked ? null : ReadValue(field);
            var hidden = linked || field.Kind == "url" && value is string { Length: > 0 };
            return new { field.Key, field.Label, field.Kind, field.Min, field.Max, field.Choices,
                value = hidden ? null : value, hidden };
        }).ToArray();
        return new { revision, fields, sources = new { localCount = ReadSources("sub-urls").Length,
            remoteCount = ReadSources("sub-urls-remote").Length } };
    }

    private object? ReadValue(Field field)
    {
        if (!values.TryGetValue(field.Key, out var value)) return null;
        if (field.Kind == "list") return ReadSources(field.Key);
        if (value is not YamlScalarNode scalar) throw new InvalidDataException($"参数 {field.Key} 不是标量，请使用原管理台处理。");
        var text = scalar.Value ?? "";
        return field.Kind switch
        {
            "integer" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number : throw new InvalidDataException($"参数 {field.Key} 不是整数。"),
            "number" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction)
                ? fraction : throw new InvalidDataException($"参数 {field.Key} 不是有效数字。"),
            "boolean" => bool.TryParse(text, out var boolean) ? boolean
                : throw new InvalidDataException($"参数 {field.Key} 不是布尔值。"),
            _ => text
        };
    }

    public string[] ReadSources(string key)
    {
        if (!values.TryGetValue(key, out var value) || value is YamlScalarNode { Value: null or "" or "null" or "~" }) return [];
        if (value is not YamlSequenceNode sequence || sequence.Children.Any(n => n is not YamlScalarNode))
            throw new InvalidDataException($"参数 {key} 不是字符串列表，请使用原管理台处理。");
        return sequence.Children.Cast<YamlScalarNode>().Select(n => n.Value ?? "").ToArray();
    }

    public string Apply(IReadOnlyDictionary<string, JsonElement> changes)
    {
        if (changes.Count == 0) throw new ArgumentException("没有需要保存的参数。");
        foreach (var (key, value) in changes)
        {
            var field = Fields.FirstOrDefault(f => f.Key == key) ?? throw new ArgumentException("不允许编辑此检测器参数。");
            Validate(field, value);
        }
        return Replace(changes);
    }

    public string AddSources(IEnumerable<string> added)
    {
        var sources = ReadSources("sub-urls").Concat(added).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length > 5000) throw new ArgumentException("追加后订阅来源超过5000条，请缩小范围。");
        return Replace(new Dictionary<string, JsonElement> { ["sub-urls"] = JsonSerializer.SerializeToElement(sources) });
    }

    private string Replace(IReadOnlyDictionary<string, JsonElement> changes)
    {
        var patches = new List<(int Start, int End, string Text)>();
        var additions = new List<string>();
        foreach (var (key, value) in changes)
        {
            // JSON scalars/arrays are valid YAML flow values and preserve their types.
            var replacement = key + ": " + value.GetRawText();
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Linked) throw new InvalidDataException($"参数 {key} 使用锚点或别名，请在原管理台修改。");
                patches.Add((entry.Start, entry.End, replacement));
            }
            else additions.Add(replacement);
        }
        if (additions.Count > 0) patches.Add((insertAt, insertAt, "\n" + string.Join('\n', additions) + "\n"));
        var result = yaml;
        foreach (var patch in patches.OrderByDescending(p => p.Start))
            result = result[..patch.Start] + patch.Text + result[patch.End..];
        _ = new CheckerConfig(result); // Never send a structurally invalid rewrite upstream.
        return result;
    }

    private static void Validate(Field field, JsonElement value)
    {
        bool valid;
        if (field.Kind is "integer" or "number")
            valid = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
                && (field.Kind != "integer" || value.TryGetInt64(out _)) && number >= field.Min && number <= field.Max;
        else if (field.Kind == "boolean") valid = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        else if (field.Kind == "list") valid = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 100 &&
            value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && ValidText(v.GetString(), 128));
        else
        {
            valid = value.ValueKind == JsonValueKind.String && ValidText(value.GetString(), 2048);
            if (valid && field.Kind == "choice") valid = field.Choices!.Contains(value.GetString(), StringComparer.Ordinal);
            if (valid && field.Kind == "url" && value.GetString() is { Length: > 0 } url)
                valid = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                    uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
            if (valid && field.Key == "sub-store-port" && value.GetString() is { Length: > 0 } port)
                valid = System.Text.RegularExpressions.Regex.IsMatch(port, @"^(?:(?:127\.0\.0\.1|0\.0\.0\.0)?):[0-9]{1,5}$") &&
                    int.TryParse(port[(port.LastIndexOf(':') + 1)..], out var portNumber) && portNumber is >= 1 and <= 65535;
        }
        if (!valid) throw new ArgumentException($"参数 {field.Key} 的类型、范围或格式不正确；含凭据地址请在原管理台配置。");
    }

    private static bool ValidText(string? value, int maximum) => value is not null && value.Length <= maximum && !value.Any(char.IsControl);
}
