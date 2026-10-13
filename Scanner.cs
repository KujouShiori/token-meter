using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TokenMeter;

/// <summary>
/// 单个日志文件的增量状态 + 它自己贡献的「日期 -&gt; 模型 -&gt; 计数」桶。
/// 桶必须按文件保存，否则下次只读新增字节时无法把这个文件的既有贡献重新合并进账本。
/// </summary>
internal sealed class FileBucket
{
    /// <summary>已消费到的字节偏移，永远停在换行符边界上。</summary>
    public long Offset { get; set; }

    public Dictionary<string, Dictionary<string, long[]>> Days { get; set; } = new(StringComparer.Ordinal);

    public void Add(string dayKey, string model, Counters c)
    {
        if (!Days.TryGetValue(dayKey, out var byModel))
        {
            byModel = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            Days[dayKey] = byModel;
        }
        if (!byModel.TryGetValue(model, out var arr))
        {
            byModel[model] = c.ToArray();
            return;
        }
        if (arr.Length < 6) arr = new long[6];
        arr[0] += c.Input;
        arr[1] += c.Output;
        arr[2] += c.CacheRead;
        arr[3] += c.CacheWrite;
        arr[4] += c.Total;
        arr[5] += c.Requests;
    }

    public void Reset()
    {
        Offset = 0;
        Days.Clear();
    }
}

internal sealed class ScanCache
{
    public int Version { get; set; } = 1;
    public Dictionary<string, FileBucket> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 会话日志扫描器。
///
/// 为什么必须做成增量的：本机实测 117 个 .jsonl、合计 175.5 MB，单行最大 4.6 MB。
/// 每次刷新全量重读会把 CPU 和磁盘打满，所以每个文件只从上次停下的字节偏移往后读，
/// 读到的完整行累加进该文件的桶，未写完的尾行不计入偏移（下次重新读它）。
/// </summary>
public sealed class UsageScanner
{
    private const int MaxFiles = 3000;
    private const int ChunkSize = 1 << 17;

    private static ReadOnlySpan<byte> UsageMark => "\"usage\":{"u8;
    private static ReadOnlySpan<byte> RoleMark => "\"role\":\"assistant\""u8;
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _cacheDir;
    private readonly string _cachePath;
    private ScanCache _cache = new();
    private bool _cacheLoaded;

    public UsageScanner(string cacheDir)
    {
        _cacheDir = cacheDir;
        _cachePath = Path.Combine(cacheDir, "scan-cache-v1.json");
    }

    /// <summary>本机默认数据根目录：本地 AI 客户端的项目会话目录。</summary>
    public static string DefaultRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";
        return Path.Combine(profile, ".sclaw", "agent", "projects");
    }

    /// <summary>解析设置里的数据目录（分号分隔，留空 = 用默认目录）。</summary>
    public static List<string> ResolveRoots(string? settingValue)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(settingValue))
        {
            foreach (var part in settingValue.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim().Trim('"');
                if (t.Length == 0) continue;
                try { list.Add(Path.GetFullPath(t)); }
                catch (Exception) { list.Add(t); }
            }
        }
        if (list.Count == 0) list.Add(DefaultRoot());
        return list;
    }

    public static string DescribeRoots(IReadOnlyList<string> roots)
        => roots.Count == 1 ? roots[0] : string.Join(" ；", roots);

    public UsageLedger Scan(IReadOnlyList<string> roots, CancellationToken ct)
    {
        EnsureCacheLoaded();

        var ledger = new UsageLedger
        {
            RootDescription = DescribeRoots(roots),
        };

        var files = CollectFiles(roots, ledger);
        var alive = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        long scanned = 0;

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!_cache.Files.TryGetValue(path, out var bucket))
            {
                bucket = new FileBucket();
                _cache.Files[path] = bucket;
            }

            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception ex) { NoteOnce(ledger, $"读不到文件信息：{ex.Message}"); continue; }

            if (length < bucket.Offset)
            {
                // 文件被截断或整体重写：这只可能让既有桶失效，重扫该文件
                bucket.Reset();
            }

            if (length > bucket.Offset)
            {
                // 先记下起点：ScanFileIncremental 会把 bucket.Offset 推进到末尾，
                // 晚一步再算 length - bucket.Offset 就永远是 0（之前就是这个 bug）
                var from = bucket.Offset;
                try
                {
                    ScanFileIncremental(path, bucket, ct);
                    scanned += length - from;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    NoteOnce(ledger, $"跳过 {Path.GetFileName(path)}：{ex.Message}");
                }
            }

            foreach (var (dayKey, byModel) in bucket.Days)
                foreach (var (model, arr) in byModel)
                    ledger.Add(dayKey, model, Counters.FromArray(arr));
        }

        // 已经消失的文件要把桶一起丢掉，否则累计值会一直虚高
        foreach (var stale in _cache.Files.Keys.Where(k => !alive.Contains(k)).ToList())
            _cache.Files.Remove(stale);

        ledger.Files = files.Count;
        ledger.ScannedBytes = scanned;
        return ledger;
    }

    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, JsonOpts), Utf8Strict);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception)
        {
            // 缓存写不下去只影响下次扫描速度，不影响本次显示，交给下一轮重试
        }
    }

    public void ForgetCache()
    {
        _cache = new ScanCache();
        _cacheLoaded = true;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;
        _cacheLoaded = true;
        try
        {
            if (File.Exists(_cachePath))
            {
                var text = File.ReadAllText(_cachePath, Utf8Strict);
                var loaded = JsonSerializer.Deserialize<ScanCache>(text, JsonOpts);
                if (loaded is not null && loaded.Version == 1)
                {
                    loaded.Files ??= new Dictionary<string, FileBucket>(StringComparer.OrdinalIgnoreCase);
                    _cache = loaded;
                }
            }
        }
        catch (Exception)
        {
            _cache = new ScanCache();
        }
    }

    private static void NoteOnce(UsageLedger ledger, string note)
    {
        if (string.IsNullOrEmpty(ledger.Note)) ledger.Note = note;
    }

    private static List<string> CollectFiles(IReadOnlyList<string> roots, UsageLedger ledger)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                NoteOnce(ledger, $"目录不存在：{root}");
                continue;
            }

            IEnumerable<string> query;
            try { query = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
            catch (Exception ex) { NoteOnce(ledger, $"列目录失败：{ex.Message}"); continue; }

            using var e = query.GetEnumerator();
            while (e.MoveNext())
            {
                if (files.Count >= MaxFiles)
                {
                    NoteOnce(ledger, $"文件数超过上限 {MaxFiles}，只统计前 {MaxFiles} 个（可在设置里缩小数据目录）");
                    return files;
                }
                if (seen.Add(e.Current)) files.Add(e.Current);
            }
        }

        return files;
    }

    private static void ScanFileIncremental(string path, FileBucket bucket, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan);

        fs.Seek(bucket.Offset, SeekOrigin.Begin);
        long pos = bucket.Offset;

        var chunk = new byte[ChunkSize];
        var line = new MemoryStream(1 << 16);
        try
        {
            int read;
            while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int seg = 0;
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n') continue;
                    if (i > seg) line.Write(chunk, seg, i - seg);
                    ProcessLine(line, bucket);
                    pos += line.Length + 1;      // +1 是换行符本身
                    line.SetLength(0);
                    seg = i + 1;
                }
                // 段尾没遇到换行 = 这行还没写完，留着下次重读，偏移不前进
                if (seg < read) line.Write(chunk, seg, read - seg);
            }
        }
        finally
        {
            line.Dispose();
        }

        bucket.Offset = pos;
    }

    private static void ProcessLine(MemoryStream line, FileBucket bucket)
    {
        var len = (int)line.Length;
        if (len < UsageMark.Length + RoleMark.Length) return;

        var buf = line.GetBuffer().AsSpan(0, len);
        if (buf.IndexOf(UsageMark) < 0) return;              // 字节级预筛：绝大多数行在这里就被甩掉
        if (buf.IndexOf(RoleMark) < 0) return;               // 只统计助手回复，避免重复计数

        string text;
        try { text = Utf8Strict.GetString(line.GetBuffer(), 0, len); }
        catch (Exception) { return; }

        var usageIdx = text.IndexOf("\"usage\":{", StringComparison.Ordinal);
        if (usageIdx < 0) return;

        var counters = ReadUsageObject(text, usageIdx + 8);
        if (counters is null) return;

        var dayKey = ReadDayKey(text);
        if (dayKey is null) return;

        bucket.Add(dayKey, ReadModelName(text, usageIdx), counters);
    }

    /// <summary>取 usage 前面最近的那个 "model":"..."（实测它就贴在 usage 前 20 字符处）。</summary>
    private static string ReadModelName(string text, int beforeIndex)
    {
        var at = text.LastIndexOf("\"model\":\"", beforeIndex, StringComparison.Ordinal);
        if (at < 0) return "(未知模型)";
        var i = at + 8;                        // 指向值的开引号
        var name = ReadJsonString(text, ref i);
        if (string.IsNullOrWhiteSpace(name)) return "(未知模型)";

        // 有的客户端把「供应商 id/模型名」整个写进 model，而单价是全等匹配的，
        // 不剥掉前缀就永远命中不了价格，界面上也只剩一串 UUID。
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];

        name = name.Trim();
        return name.Length == 0 ? "(未知模型)" : name;
    }

    /// <summary>行首的 "timestamp":"...Z" 决定这条记录算哪天（换算到本地时区）。</summary>
    private static string? ReadDayKey(string text)
    {
        var t = text.IndexOf("\"timestamp\":\"", StringComparison.Ordinal);
        if (t < 0) return null;
        var start = t + 13;
        var end = text.IndexOf('"', start);
        if (end < 0 || end - start < 10) return null;

        var raw = text.Substring(start, end - start);
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return null;

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
        return Format.DayKey(DateOnly.FromDateTime(local));
    }

    /// <summary>
    /// 按大括号深度只读 usage 对象第一层的数字键。
    /// 必须这么做：usage 里嵌套的 cost 对象有同名的 input/output/cacheRead/cacheWrite 键，
    /// 直接扫字符串会把钱当成 token 数。
    /// </summary>
    private static Counters? ReadUsageObject(string s, int openBrace)
    {
        var c = new Counters();
        var gotAny = false;
        var i = openBrace + 1;                 // 跳过 usage 的 '{'

        while (i < s.Length)
        {
            var ch = s[i];
            if (ch is ' ' or '\t' or '\r' or '\n' or ',' or ':') { i++; continue; }
            if (ch == '}') break;              // usage 对象结束
            if (ch != '"') { i++; continue; }  // 意外内容：跳过

            var key = ReadJsonString(s, ref i);
            while (i < s.Length && (s[i] == ':' || s[i] == ' ')) i++;
            if (i >= s.Length) break;

            var v = s[i];
            if (v is '{' or '[')
            {
                SkipContainer(s, ref i, v);
                continue;                      // 嵌套对象（cost 就是）整块跳过，不进去取值
            }
            if (v == '"')
            {
                ReadJsonString(s, ref i);
                continue;
            }

            var from = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            var raw = s.AsSpan(from, i - from).Trim();
            if (raw.Length == 0) continue;

            var value = ParseCount(raw);
            switch (key)
            {
                case "input":
                case "input_tokens":              // Anthropic 原生：不含缓存部分
                case "prompt_tokens":             // OpenAI Chat：已含缓存，所以不再另加 cacheRead
                    c.Input = value; gotAny = true; break;
                case "output":
                case "output_tokens":
                case "completion_tokens":
                    c.Output = value; gotAny = true; break;
                case "cacheRead":
                case "cache_read_input_tokens":
                    c.CacheRead = value; gotAny = true; break;
                case "cacheWrite":
                case "cache_creation_input_tokens":
                    c.CacheWrite = value; gotAny = true; break;
                case "totalTokens":
                case "total_tokens":
                    c.Total = value; gotAny = true; break;
                default: break;                 // reasoning 已含在 output 里，cost 是钱，都不取
            }
        }

        if (!gotAny) return null;
        if (c.Total <= 0) c.Total = c.Input + c.Output + c.CacheRead + c.CacheWrite;
        c.Requests = 1;
        return c;
    }

    private static long ParseCount(ReadOnlySpan<char> raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && !double.IsNaN(d) && !double.IsInfinity(d))
            return (long)Math.Round(d);
        return 0;
    }

    /// <summary>读一个 JSON 字符串（i 指向开引号），返回后 i 越过闭引号。</summary>
    private static string ReadJsonString(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"') { i++; return string.Empty; }
        i++;
        var sb = new StringBuilder(32);
        while (i < s.Length)
        {
            var ch = s[i++];
            if (ch == '"') break;
            if (ch != '\\') { sb.Append(ch); continue; }
            if (i >= s.Length) break;
            var esc = s[i++];
            switch (esc)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 <= s.Length && int.TryParse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                    { sb.Append((char)cp); i += 4; }
                    else sb.Append('u');
                    break;
                default: sb.Append(esc); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>整体跳过 {…} 或 […]，正确处理字符串内部的括号和转义。</summary>
    private static void SkipContainer(string s, ref int i, char open)
    {
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inStr = false;
        var esc = false;
        while (i < s.Length)
        {
            var ch = s[i++];
            if (inStr)
            {
                if (esc) { esc = false; continue; }
                if (ch == '\\') { esc = true; continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == open) depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0) return;
            }
        }
    }
}
