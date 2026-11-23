using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public enum AuthKind
{
    None,
    Basic,
    Bearer,
    Digest,
    Jwt,
    OAuth,
}

/// <summary>认证凭据。Bearer / Jwt / OAuth 都走 Authorization: Bearer，Digest 交给 HttpClientHandler 处理 401 挑战。</summary>
public sealed record AuthSpec(AuthKind Kind, string User, string Secret)
{
    public string? HeaderValue => Kind switch
    {
        AuthKind.Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}")),
        AuthKind.Bearer or AuthKind.Jwt or AuthKind.OAuth => $"Bearer {Secret}",
        _ => null,
    };

    [JsonIgnore]
    public NetworkCredential? NetworkCredential => Kind == AuthKind.Digest ? new NetworkCredential(User, Secret) : null;
}

/// <summary>HTTPie 风格的会话文件：持久化的头、Cookie、认证。</summary>
public sealed class SessionData
{
    public Dictionary<string, List<string>> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AuthSpec? Auth { get; set; }
}

/// <summary>构建 RequestContext 所需的全部输入。</summary>
public sealed class RequestContextOptions
{
    public List<string> RawHeaders { get; set; } = new();
    public string? AuthArg { get; set; }
    public string? AuthTypeArg { get; set; }
    public string? SessionArg { get; set; }
    public bool UseNetrc { get; set; } = true;
    public bool Verbose { get; set; }
    public int MaxRedirects { get; set; } = 10;
    public Action<string>? Trace { get; set; }
    public DownloadConfig Config { get; set; } = new();
}

/// <summary>
/// 一次运行共享的请求策略。被 RequestPipelineHandler 用来给每一个出站请求加头、加认证、加 Cookie，
/// 并从响应里收集 Set-Cookie。所有集合都按 host 隔离或线程安全，供并行下载共用。
/// </summary>
public sealed class RequestContext
{
    static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly HashSet<string> NonSessionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "User-Agent", "Content-Length", "Accept-Encoding", "Connection",
        "Cookie", "Set-Cookie", "Proxy-Authorization",
    };

    static readonly Regex HeaderPattern = new(
        @"^\s*([!#$%&'*+\-.^_`|~0-9A-Za-z]+)\s*:\s*(.*?)\s*$",
        RegexOptions.Compiled);

    readonly Dictionary<string, List<string>> _headers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _cookies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AuthSpec> _hostCredentials = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _redirects = new();
    readonly object _sessionLock = new();
    readonly string? _sessionPath;
    readonly SessionData _session;

    AuthSpec? _auth;

    RequestContext(string? sessionPath, SessionData session)
    {
        _sessionPath = sessionPath;
        _session = session;
    }

    public AuthSpec? Auth => _auth;

    public NetworkCredential? DigestCredential => _auth?.NetworkCredential;

    public bool Verbose { get; private set; }

    public int MaxRedirects { get; private set; } = 10;

    public Action<string>? Trace { get; private set; }

    public IReadOnlyList<string> Redirects
    {
        get { lock (_sessionLock) return _redirects.ToArray(); }
    }

    /// <summary>当前生效的全部头（供 --offline 打印）。</summary>
    public IReadOnlyDictionary<string, List<string>> Headers => _headers;

    /// <summary>
    /// 按 CLI &gt; 会话 &gt; netrc &gt; 配置 的优先级合并。后用覆盖先用。
    /// </summary>
    public static RequestContext Create(RequestContextOptions options, IReadOnlyList<string> urls, List<string> notes)
    {
        var config = options.Config ?? new DownloadConfig();
        var sessionArg = options.SessionArg ?? config.Session;
        var sessionPath = ResolveSessionPath(sessionArg);
        var session = sessionPath is null ? new SessionData() : LoadSession(sessionPath, notes);

        var context = new RequestContext(sessionPath, session)
        {
            Verbose = options.Verbose || config.Verbose,
            MaxRedirects = options.MaxRedirects > 0 ? options.MaxRedirects : config.MaxRedirects,
            Trace = options.Trace,
        };

        // 1) 配置文件里的头（最低优先级）
        foreach (var (name, value) in config.Headers)
            context.AddHeader(name, value);

        // 2) 会话文件里的头
        foreach (var (name, values) in session.Headers)
            foreach (var value in values)
                context.AddHeader(name, value);

        // 3) 认证：CLI > 会话 > 配置 > netrc
        var auth = ParseAuth(options.AuthArg, options.AuthTypeArg, notes)
                   ?? ParseAuth(session.Auth is { Kind: not AuthKind.None } ? Describe(session.Auth) : null, null, notes)
                   ?? ParseAuth(config.Auth, config.AuthType, notes);

        if (auth is not null && auth.Kind != AuthKind.None)
            context._auth = auth;
        else
            context._auth = session.Auth is { Kind: not AuthKind.None } ? session.Auth : null;

        if (context._auth?.HeaderValue is { } authHeader && !context.HasHeader("Authorization"))
            context.AddHeader("Authorization", authHeader);

        // 4) netrc：按 host 存，仅在该 host 没有 Authorization 时生效
        if (options.UseNetrc && config.UseNetrc)
        {
            foreach (var (host, cred) in NetrcParser.Load(urls, notes))
                context._hostCredentials[host] = cred;
        }

        // 5) CLI -H（最高优先级）
        foreach (var raw in options.RawHeaders)
        {
            if (!TryParseHeader(raw, out var name, out var value))
            {
                notes.Add($"忽略无效的 -H 参数：{raw}（格式应为 \"Name: value\"）");
                continue;
            }
            context.AddHeader(name, value, replace: true);
        }

        // 会话 Cookie 灌入内存 jar
        foreach (var (host, cookies) in session.Cookies)
        {
            var jar = context._cookies.GetOrAdd(host, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            foreach (var (name, value) in cookies)
                jar[name] = value;
        }

        return context;
    }

    static string Describe(AuthSpec auth) => auth.Kind switch
    {
        AuthKind.Basic => $"basic:{auth.User}:{auth.Secret}",
        _ => $"{auth.Kind.ToString().ToLowerInvariant()}:{auth.Secret}",
    };

    public static AuthSpec? ParseAuth(string? authArg, string? typeArg, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(authArg)) return null;

        var kind = ParseAuthKind(typeArg) ?? AuthKind.Basic;
        var raw = authArg.Trim();

        if (kind is AuthKind.Bearer or AuthKind.Jwt or AuthKind.OAuth)
            return new AuthSpec(kind, "", raw);

        var colon = raw.IndexOf(':');
        if (colon < 0)
            return new AuthSpec(kind, raw, "");

        return new AuthSpec(kind, raw[..colon], raw[(colon + 1)..]);
    }

    static AuthKind? ParseAuthKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "basic" => AuthKind.Basic,
            "bearer" => AuthKind.Bearer,
            "digest" => AuthKind.Digest,
            "jwt" => AuthKind.Jwt,
            "oauth" or "oauth2" => AuthKind.OAuth,
            _ => null,
        };
    }

    public static bool TryParseHeader(string raw, out string name, out string value)
    {
        name = "";
        value = "";
        var match = HeaderPattern.Match(raw ?? "");
        if (!match.Success) return false;
        name = match.Groups[1].Value;
        value = match.Groups[2].Value;
        return true;
    }

    void AddHeader(string name, string value, bool replace = false)
    {
        if (!replace && HasHeader(name)) return;
        lock (_sessionLock)
        {
            if (replace) _headers[name] = new List<string> { value };
            else if (!_headers.TryGetValue(name, out var list))
                _headers[name] = new List<string> { value };
            else if (!list.Contains(value))
                list.Add(value);
        }
    }

    bool HasHeader(string name)
    {
        lock (_sessionLock) return _headers.ContainsKey(name);
    }

    /// <summary>返回要加到该请求上的头。认证按 host 决定：全局 --auth 已在 Headers 里，netrc 按域名补充。</summary>
    public IEnumerable<KeyValuePair<string, string>> HeadersFor(Uri uri)
    {
        List<KeyValuePair<string, string>> result;
        lock (_sessionLock)
        {
            result = _headers.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v))).ToList();
        }

        if (!result.Any(kv => kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            && _hostCredentials.TryGetValue(uri.Host, out var hostAuth)
            && hostAuth.HeaderValue is { } value)
            result.Add(new KeyValuePair<string, string>("Authorization", value));

        var cookie = CookieHeaderFor(uri);
        if (!string.IsNullOrEmpty(cookie) && !result.Any(kv => kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            result.Add(new KeyValuePair<string, string>("Cookie", cookie));

        return result;
    }

    public string? CookieHeaderFor(Uri uri)
    {
        if (!_cookies.TryGetValue(uri.Host, out var jar)) return null;
        var parts = jar.Select(kv => $"{kv.Key}={kv.Value}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return parts.Length == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>从响应里收下 Set-Cookie。属性（Path/Domain/Expires）不解析，按响应所在 host 存。</summary>
    public void CaptureCookies(Uri uri, IEnumerable<string> values)
    {
        var jar = _cookies.GetOrAdd(uri.Host, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        foreach (var raw in values)
        {
            var first = raw.Split(';', 2)[0];
            var eq = first.IndexOf('=');
            if (eq <= 0) continue;

            var name = first[..eq].Trim();
            var value = first[(eq + 1)..].Trim().Trim('"');
            if (name.Length == 0) continue;

            // 过期或清空
            if (raw.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                jar.TryRemove(name, out _);
            else
                jar[name] = value;
        }
    }

    public void RecordRedirect(string from, string to, int status, bool crossHost)
    {
        if (!Verbose) return;
        lock (_sessionLock)
            _redirects.Add($"{status} {from} -> {to}" + (crossHost ? " (cross-host, credentials stripped)" : ""));
    }

    /// <summary>敏感头打码：保留键名，值只显示长度。</summary>
    public static string Redact(string name, string value)
    {
        if (IsSensitive(name)) return $"*** ({Encoding.UTF8.GetByteCount(value)} 字节)";
        return value;
    }

    public static bool IsSensitive(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("key", StringComparison.OrdinalIgnoreCase)
           || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>退出时把 Cookie 和头写回会话文件。没有 --session 就什么也不做。</summary>
    public void SaveSession()
    {
        if (_sessionPath is null) return;

        try
        {
            var data = new SessionData
            {
                Auth = _auth,
            };

            lock (_sessionLock)
            {
                foreach (var (name, values) in _headers)
                {
                    if (NonSessionHeaders.Contains(name)) continue;
                    data.Headers[name] = new List<string>(values);
                }

                foreach (var (host, jar) in _cookies)
                    data.Cookies[host] = jar.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            }

            var dir = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_sessionPath, JsonSerializer.Serialize(data, SessionJsonOptions), new UTF8Encoding(false));
            Trace?.Invoke($"会话已保存：{_sessionPath}");
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"! 会话保存失败：{EscapeLite(ex.Message)}");
        }
    }

    static string EscapeLite(string value) => value.Replace("[", "\u3010").Replace("]", "\u3011");

    static string? ResolveSessionPath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        var value = arg.Trim();

        // 显式路径：含分隔符或以 .json 结尾
        if (value.Contains('/') || value.Contains('\\') || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(value);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) appData = Environment.CurrentDirectory;
        return Path.Combine(appData, "SuDown", "sessions", value + ".json");
    }

    static SessionData LoadSession(string path, List<string> notes)
    {
        if (!File.Exists(path)) return new SessionData();

        try
        {
            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(path), SessionJsonOptions);
            if (data is null) return new SessionData();

            data.Headers ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            data.Cookies ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (Exception ex)
        {
            notes.Add($"会话文件读取失败（{path}）：{ex.Message}，按空会话处理");
            return new SessionData();
        }
    }
}

/// <summary>~/.netrc 解析器。只取 machine/login/password，忽略 account/macdef。</summary>
public static class NetrcParser
{
    public static Dictionary<string, AuthSpec> Load(IReadOnlyList<string> urls, List<string> notes)
    {
        var result = new Dictionary<string, AuthSpec>(StringComparer.OrdinalIgnoreCase);
        var path = FindNetrc();
        if (path is null) return result;

        try
        {
            var tokens = Tokenize(File.ReadAllText(path));
            var index = 0;
            string? pendingMachine = null;
            string? login = null;
            string? password = null;
            AuthSpec? defaultAuth = null;

            void Flush()
            {
                if (login is null) return;
                var cred = new AuthSpec(AuthKind.Basic, login, password ?? "");
                if (pendingMachine is not null) result[pendingMachine] = cred;
                else defaultAuth = cred;
            }

            while (index < tokens.Count)
            {
                switch (tokens[index++])
                {
                    case "machine":
                        Flush();
                        pendingMachine = index < tokens.Count ? tokens[index++] : null;
                        login = password = null;
                        break;
                    case "default":
                        Flush();
                        pendingMachine = null;
                        login = password = null;
                        break;
                    case "login":
                        login = index < tokens.Count ? tokens[index++] : null;
                        break;
                    case "password":
                        password = index < tokens.Count ? tokens[index++] : null;
                        break;
                    case "account":
                    case "macdef":
                        if (index < tokens.Count) index++; // 跳过未知指令的值
                        break;
                }
            }

            Flush();

            // 只保留本次下载实际用到的 host，避免无关凭据常驻
            var hosts = new HashSet<string>(urls.Select(u =>
            {
                try { return new Uri(u).Host; } catch { return ""; }
            }), StringComparer.OrdinalIgnoreCase);

            // default 条目兜底给所有没有单独 machine 的 host
            if (defaultAuth is not null)
                foreach (var host in hosts)
                    result.TryAdd(host, defaultAuth);

            return result.Where(kv => hosts.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            notes.Add($"读取 netrc 失败（{path}）：{ex.Message}");
            return new Dictionary<string, AuthSpec>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    static string? FindNetrc()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        foreach (var name in new[] { "_netrc", ".netrc" })
        {
            var path = Path.Combine(home, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
