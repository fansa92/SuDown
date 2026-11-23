using System.Text.Json;

/// <summary>
/// 下载配置。所有字段都有安全默认值，配置文件缺失或部分字段缺失时不会崩。
/// </summary>
public sealed class DownloadConfig
{
    public int MaxConnections { get; set; } = 4;

    /// <summary>同时下载的文件数（-j）。每个文件内部仍按 MaxConnections 分片，-s 是所有文件共享的总限速。</summary>
    public int MaxParallelFiles { get; set; } = 1;

    public long MinSizeForMultiThread { get; set; } = 1024 * 1024;

    /// <summary>建立 TCP/TLS 连接的超时。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>单次读取的空闲超时：连续这么久没收到任何数据才判定超时。取代原来的整体请求超时。</summary>
    public int ReadIdleTimeoutSeconds { get; set; } = 60;

    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 2;
    public string UserAgent { get; set; } = "SuDown/2.0";
    public bool EnableResume { get; set; } = true;
    public bool EnableMultiThread { get; set; } = true;

    public bool ProxyEnabled { get; set; }
    public string ProxyAddress { get; set; } = "";
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    /// <summary>默认请求头（所有请求都会带上，命令行 -H 优先）。</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认认证，写法同命令行 --auth（如 basic:user:pass 或 bearer:token）。</summary>
    public string? Auth { get; set; }
    public string? AuthType { get; set; }

    /// <summary>默认会话名或路径。</summary>
    public string? Session { get; set; }

    /// <summary>是否自动读取 ~/.netrc。</summary>
    public bool UseNetrc { get; set; } = true;

    public bool Verbose { get; set; }
    public int MaxRedirects { get; set; } = 10;

    /// <summary>实际加载到的配置文件路径，null 表示用的内置默认值。</summary>
    public string? LoadedFrom { get; set; }

    public void Normalize()
    {
        MaxConnections = Math.Clamp(MaxConnections, 1, 32);
        MaxParallelFiles = Math.Clamp(MaxParallelFiles, 1, 16);
        MinSizeForMultiThread = Math.Clamp(MinSizeForMultiThread, 256 * 1024, 1024L * 1024 * 1024);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 1, 600);
        ReadIdleTimeoutSeconds = Math.Clamp(ReadIdleTimeoutSeconds, 1, 3600);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelaySeconds = Math.Clamp(RetryDelaySeconds, 0, 60);
        if (string.IsNullOrWhiteSpace(UserAgent)) UserAgent = "SuDown/2.0";
        MaxRedirects = Math.Clamp(MaxRedirects, 0, 50);
    }
}

/// <summary>
/// 配置加载器。查找顺序：显式 --config → 当前目录 → 程序目录 → %APPDATA%\SuDown\config.json。
/// 逐字段读取，缺字段沿用默认值；解析失败也不会静默退回默认值，而是给出提示。
/// </summary>
public static class ConfigLoader
{
    public static DownloadConfig Load(string? explicitPath, List<string> notes)
    {
        var config = new DownloadConfig();
        var path = Resolve(explicitPath, notes);

        if (path is null)
        {
            notes.Add("未找到配置文件，使用内置默认值");
            config.Normalize();
            return config;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("Download", out var dl) && dl.ValueKind == JsonValueKind.Object)
            {
                config.MaxConnections = ReadInt(dl, "MaxConnections", config.MaxConnections);
                config.MaxParallelFiles = ReadInt(dl, "MaxParallelFiles", config.MaxParallelFiles);
                config.MinSizeForMultiThread = ReadLong(dl, "MinSizeForMultiThread", config.MinSizeForMultiThread);
                config.ConnectTimeoutSeconds = ReadInt(dl, "ConnectTimeoutSeconds", config.ConnectTimeoutSeconds);
                // 兼容旧配置里的 TimeoutSeconds
                config.ReadIdleTimeoutSeconds = ReadInt(dl, "ReadIdleTimeoutSeconds",
                    ReadInt(dl, "TimeoutSeconds", config.ReadIdleTimeoutSeconds));
                config.MaxRetries = ReadInt(dl, "MaxRetries", config.MaxRetries);
                config.RetryDelaySeconds = ReadInt(dl, "RetryDelaySeconds", config.RetryDelaySeconds);
                config.UserAgent = ReadString(dl, "UserAgent", config.UserAgent);
                config.EnableResume = ReadBool(dl, "EnableResume", config.EnableResume);
                config.EnableMultiThread = ReadBool(dl, "EnableMultiThread", config.EnableMultiThread);
            }

            if (root.TryGetProperty("Proxy", out var px) && px.ValueKind == JsonValueKind.Object)
            {
                config.ProxyEnabled = ReadBool(px, "Enabled", config.ProxyEnabled);
                config.ProxyAddress = ReadString(px, "Address", config.ProxyAddress);
                config.ProxyUsername = ReadString(px, "Username", config.ProxyUsername);
                config.ProxyPassword = ReadString(px, "Password", config.ProxyPassword);
            }

            if (root.TryGetProperty("Request", out var rq) && rq.ValueKind == JsonValueKind.Object)
            {
                config.Auth = Nullify(ReadString(rq, "Auth", config.Auth ?? ""));
                config.AuthType = Nullify(ReadString(rq, "AuthType", config.AuthType ?? ""));
                config.Session = Nullify(ReadString(rq, "Session", config.Session ?? ""));
                config.UseNetrc = ReadBool(rq, "UseNetrc", config.UseNetrc);
                config.Verbose = ReadBool(rq, "Verbose", config.Verbose);
                config.MaxRedirects = ReadInt(rq, "MaxRedirects", config.MaxRedirects);

                if (rq.TryGetProperty("Headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var header in hs.EnumerateObject())
                    {
                        if (header.Value.ValueKind == JsonValueKind.String)
                            config.Headers[header.Name] = header.Value.GetString() ?? "";
                        else if (header.Value.ValueKind == JsonValueKind.Array)
                            config.Headers[header.Name] = string.Join(",", header.Value.EnumerateArray()
                                .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()));
                    }
                }
            }

            config.LoadedFrom = path;
        }
        catch (JsonException ex)
        {
            notes.Add($"配置文件不是合法 JSON（{path}）：{ex.Message}，已回退默认值");
        }
        catch (Exception ex)
        {
            notes.Add($"读取配置失败（{path}）：{ex.Message}，已回退默认值");
        }

        config.Normalize();
        return config;
    }

    static string? Resolve(string? explicitPath, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (File.Exists(full)) return full;
            notes.Add($"指定的配置文件不存在：{full}");
            return null;
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        };

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            candidates.Add(Path.Combine(appData, "SuDown", "config.json"));

        return candidates.FirstOrDefault(File.Exists);
    }

    static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    static long ReadLong(JsonElement obj, string name, long fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : fallback;

    static string ReadString(JsonElement obj, string name, string fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

    static string? Nullify(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static bool ReadBool(JsonElement obj, string name, bool fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
