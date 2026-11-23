using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

// ============== 入口 ==============
// 统一用 UTF-8 输出，避免中文在 Git Bash / VS Code 终端里乱码。
// 设置 OutputEncoding 会同时切换控制台代码页，所以 cmd/PowerShell 也能正常显示。
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("su");
    config.AddCommand<DownloadCommand>("download")
        .WithDescription("下载一个或多个文件（默认命令，可直接 su <url>）")
        .WithExample("https://example.com/file.zip")
        .WithExample("https://a.com/1.zip", "https://b.com/2.zip", "-d", "D:\\dl")
        .WithExample("-l", "urls.txt", "-d", "D:\\dl")
        .WithExample("https://a.com/file.zip", "-m", "https://b.com/file.zip", "-m", "https://c.com/file.zip", "-d", "D:\\dl");
});

// download 是默认命令，`su <url>` 可直接运行。
// 这里把「空参数 / 纯 --help」也导向 download 的完整帮助，
// 这样 su --help 能直接看到全部选项说明，而不是只剩空壳命令列表。
var runArgs = args;
bool isPureHelp = args.Length == 0
    || (args.Length == 1 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?" || args[0] == "help"));

if (isPureHelp)
{
    runArgs = new[] { "download", "--help" };
}
else if (args[0] != "download" && args[0] != "--version" && args[0] != "-v")
{
    // su <url> ... → 自动补全为 download 子命令
    var newArgs = new List<string> { "download" };
    newArgs.AddRange(args);
    runArgs = newArgs.ToArray();
}

return app.Run(runArgs);

// ============== 命令行参数 ==============
public class DownloadSettings : CommandSettings
{
    [CommandArgument(0, "[URLS]")]
    [Description("要下载的 URL，支持多个，用空格分隔")]
    public string[] Urls { get; init; } = Array.Empty<string>();

    [CommandOption("-l|--list")]
    [Description("从文件批量读取 URL（每行一个，# 开头为注释）")]
    public string? ListFile { get; init; }

    [CommandOption("-d|--dir")]
    [Description("指定保存目录，默认当前目录")]
    public string? OutputDir { get; init; }

    [CommandOption("--log")]
    [Description("日志文件输出路径")]
    public string? LogFile { get; init; }

    [CommandOption("-s|--max-speed")]
    [Description("总限速，字节/秒（例如 102400 表示 100 KB/s，所有连接合计）")]
    public long MaxSpeed { get; init; }

    [CommandOption("-c|--connections")]
    [Description("单个文件的最大连接数，覆盖配置")]
    public int? Connections { get; init; }

    [CommandOption("-j|--parallel-files")]
    [Description("同时下载的文件数（默认 1 逐个下载；-s 限速为所有文件共享的总量）")]
    public int? ParallelFiles { get; init; }

    [CommandOption("-m|--mirror")]
    [Description("镜像源 URL，可重复传入；与唯一的主 URL 下载同一文件，多源分摊连接、单源故障自动切换")]
    public string[] Mirrors { get; init; } = Array.Empty<string>();

    [CommandOption("--no-resume")]
    [Description("禁用断点续传，从头下载")]
    public bool NoResume { get; init; }

    [CommandOption("-f|--overwrite")]
    [Description("强制重新下载，覆盖目标位置已存在的文件")]
    public bool Overwrite { get; init; }

    [CommandOption("--single")]
    [Description("强制单线程下载")]
    public bool ForceSingleThread { get; init; }

    [CommandOption("--retries")]
    [Description("失败重试次数，覆盖配置")]
    public int? Retries { get; init; }

    [CommandOption("--idle-timeout")]
    [Description("读空闲超时秒数：连续这么久没数据才算超时，覆盖配置")]
    public int? IdleTimeoutSeconds { get; init; }

    [CommandOption("--config")]
    [Description("指定配置文件路径")]
    public string? ConfigPath { get; init; }

    [CommandOption("--sha256")]
    [Description("下载完成后校验 SHA-256，不匹配则删除坏文件并视为失败")]
    public string? ExpectedSha256 { get; init; }

    [CommandOption("-H|--header")]
    [Description("自定义请求头 Name: Value，可重复；用于防盗链 Referer、Cookie、API Key 等")]
    public string[] Headers { get; init; } = Array.Empty<string>();

    [CommandOption("-a|--auth")]
    [Description("认证凭据：user:pass（Basic）或 token（Bearer）；配合 --auth-type 使用")]
    public string? Auth { get; init; }

    [CommandOption("-A|--auth-type")]
    [Description("认证方式：basic|bearer|digest|jwt|oauth，默认 basic")]
    public string? AuthType { get; init; }

    [CommandOption("-o|--output")]
    [Description("精确指定输出文件路径（仅单文件下载；与 -d 冲突时以本项为准）")]
    public string? OutputPath { get; init; }

    [CommandOption("--verbose")]
    [Description("显示实际发出的请求头与响应头（敏感值自动打码），并打印重定向链（-v 是版本，用本项全称）")]
    public bool Verbose { get; init; }

    [CommandOption("--offline")]
    [Description("只打印将要发送的请求，不发起网络请求、不下载")]
    public bool Offline { get; init; }

    [CommandOption("--session")]
    [Description("会话名或 .json 路径：跨请求复用并持久化 Cookie、自定义头与认证")]
    public string? Session { get; init; }

    [CommandOption("--no-netrc")]
    [Description("禁用自动读取 ~/.netrc 凭据")]
    public bool NoNetrc { get; init; }

    [CommandOption("--max-redirects")]
    [Description("最多跟随多少次重定向，0 表示不跟随；默认 10")]
    public int? MaxRedirects { get; init; }
}

// ============== 下载命令 ==============
[Description("下载一个或多个文件（默认命令，可直接 su <url>）")]
public class DownloadCommand : AsyncCommand<DownloadSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DownloadSettings settings)
    {
        var notes = new List<string>();
        var config = ConfigLoader.Load(settings.ConfigPath, notes);
        ApplyOverrides(config, settings);

        var log = new RunLog(settings.LogFile);

        foreach (var note in notes)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(note)}");

        if (config.LoadedFrom is not null)
            log.Info($"配置文件：{config.LoadedFrom}");

        var urls = CollectUrls(settings, log);
        if (urls.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]未提供下载 URL，请使用 --help 查看用法[/]");
            return 1;
        }

        var mirrorUrls = CollectMirrorUrls(settings, urls, log);
        if (mirrorUrls is null)
        {
            AnsiConsole.MarkupLine("[red]--mirror 只支持单文件下载：镜像 URL 与主 URL 指向同一文件，多个下载目标时无法归属[/]");
            return 1;
        }

        // -o 精确输出只能用于单文件（和 --mirror 同理，多目标无法归属到一个路径）
        if (!string.IsNullOrWhiteSpace(settings.OutputPath) && urls.Count != 1)
        {
            AnsiConsole.MarkupLine("[red]-o/--output 只支持单文件下载；批量请用 -d 指定目录[/]");
            return 1;
        }

        var notesFromRequests = new List<string>();
        var requestContext = RequestContext.Create(new RequestContextOptions
        {
            Config = config,
            RawHeaders = settings.Headers.ToList(),
            AuthArg = settings.Auth,
            AuthTypeArg = settings.AuthType,
            SessionArg = settings.Session,
            UseNetrc = !settings.NoNetrc,
            Verbose = settings.Verbose,
            MaxRedirects = settings.MaxRedirects ?? 10,
            // verbose 写到 stderr：既不污染 stdout 的进度条，也能被 2> 重定向抓取。
            Trace = message => { Console.Error.WriteLine(message); log.Info(message); },
        }, urls, notesFromRequests);

        foreach (var note in notesFromRequests)
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(note)}");

        if (settings.Offline)
        {
            PrintOfflineRequests(urls, requestContext);
            requestContext.SaveSession();
            return 0;
        }

        var outputDir = string.IsNullOrWhiteSpace(settings.OutputDir)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(settings.OutputDir);

        Directory.CreateDirectory(outputDir);

        using var http = CreateHttpClient(config, requestContext);
        using var cts = new CancellationTokenSource();
        var cancelRequests = 0;

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (Interlocked.Increment(ref cancelRequests) > 1) return;
            e.Cancel = true;
            cts.Cancel();
            log.Info("收到取消请求，正在保存续传状态（再次 Ctrl+C 强制退出）");
        };

        Console.CancelKeyPress += onCancel;

        var counters = new DownloadCounters();

        // 所有下载共享同一个限速桶：-s 是整批文件的总上限，不是每个文件各一份。
        var sharedLimiter = settings.MaxSpeed > 0 ? new TokenBucketRateLimiter(settings.MaxSpeed) : null;

        try
        {
            // 镜像下载只针对单文件，走串行分支即可（文件内部分片已经是多连接）。
            if (config.MaxParallelFiles <= 1 || mirrorUrls.Count > 0)
            {
                foreach (var url in urls)
                {
                    if (cts.IsCancellationRequested)
                    {
                        counters.AddInterrupted();
                        continue;
                    }

                    AnsiConsole.MarkupLine($"[grey]→[/] {Markup.Escape(Shorten(url))}");

                    try
                    {
                        var info = await RemoteProbe.ProbeAsync(http, url, cts.Token);
                        var (resolvedDir, fileName) = ResolveOutputName(settings, outputDir, info, url);
                        var target = FileTarget.Resolve(resolvedDir, fileName, info.Length, info.SupportsRanges, config.EnableResume, settings.Overwrite);

                        if (target.AlreadyComplete)
                        {
                            counters.AddSkipped();
                            AnsiConsole.MarkupLine($"  [grey]已存在且完整，跳过：[/]{Markup.Escape(fileName)}");
                            log.Info($"跳过（已存在且完整）：{target.Path}");
                            continue;
                        }

                        // 镜像只在真正要下载时才探测，跳过的文件不浪费请求。
                        List<RemoteFileInfo> healthyMirrors = new();
                        if (mirrorUrls.Count > 0)
                            healthyMirrors = await MirrorResolver.ResolveAsync(http, info, mirrorUrls, log.Info, cts.Token);

                        var outcome = await DownloadWithProgressAsync(http, config, sharedLimiter, settings, info, url, target.Path, log, cts.Token, healthyMirrors);

                        var verificationError = await VerifyAsync(target.Path, info, settings, cts.Token);
                        if (verificationError is not null)
                        {
                            counters.AddFailed();
                            var removed = DeleteCorrupted(target.Path, log);
                            AnsiConsole.MarkupLine(removed
                                ? $"  [red]× 校验失败：[/]{Markup.Escape(verificationError)}[grey]（坏文件已删除，重跑会重新下载）[/]"
                                : $"  [red]× 校验失败：[/]{Markup.Escape(verificationError)}");
                            log.Info($"校验失败：{target.Path}，{verificationError}");
                            continue;
                        }

                        counters.AddCompleted();
                        var finalSize = new FileInfo(target.Path).Length;
                        AnsiConsole.MarkupLine($"  [green]√ {Markup.Escape(Path.GetFileName(target.Path))}[/] [grey]({SizeFormatter.Format(finalSize)})[/]");
                        log.Info($"完成：{target.Path}（{finalSize} 字节）");
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        counters.AddInterrupted();
                        log.Info($"已取消：{url}");
                        AnsiConsole.MarkupLine($"  [yellow]■ 已取消，续传状态已保存[/]");
                    }
                    catch (Exception ex)
                    {
                        counters.AddFailed();
                        log.Info($"失败：{url}，{ex.GetType().Name}: {ex.Message}");
                        AnsiConsole.MarkupLine($"  [red]× 失败：[/]{Markup.Escape(ex.Message)}");
                    }
                }
            }
            else
            {
                await DownloadInParallelAsync(http, config, settings, sharedLimiter, urls, outputDir, log, counters, cts);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            requestContext.SaveSession();
        }

        if (urls.Count > 1 || counters.Failed > 0 || counters.Interrupted > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"完成 [green]{counters.Completed}[/] · 跳过 [grey]{counters.Skipped}[/] · " +
                $"失败 [red]{counters.Failed}[/] · 中断 [yellow]{counters.Interrupted}[/]");
        }

        if (counters.Interrupted > 0) return 130;
        return counters.Failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// 并行模式：所有文件共用一个进度区，每个文件一条任务，最多 MaxParallelFiles 个同时下载。
    /// 过程日志照旧只写文件；单文件失败在进度区里标红，结束后再逐条打印明细。
    /// </summary>
    static async Task DownloadInParallelAsync(
        HttpClient http,
        DownloadConfig config,
        DownloadSettings settings,
        TokenBucketRateLimiter? limiter,
        List<string> urls,
        string outputDir,
        RunLog log,
        DownloadCounters counters,
        CancellationTokenSource cts)
    {
        AnsiConsole.MarkupLine($"[grey]并行下载 {urls.Count} 个文件，最多 {config.MaxParallelFiles} 个同时[/]");

        // 同一批文件可能解析出同名目标（多个 URL 都没给文件名时），先认领者得，后者自动改名。
        // 不加这个，两个并行下载会写坏同一个文件。
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimLock = new object();
        var failures = new ConcurrentBag<string>();

        async Task DownloadOneAsync(ProgressContext ctx, string url)
        {
            string displayName = "";
            ProgressTask? task = null;

            try
            {
                var info = await RemoteProbe.ProbeAsync(http, url, cts.Token);
                var fileName = FileNameResolver.Resolve(info, url);
                displayName = fileName;

                string targetPath;
                bool alreadyComplete;
                lock (claimLock)
                {
                    var target = FileTarget.Resolve(outputDir, fileName, info.Length, info.SupportsRanges,
                        config.EnableResume, settings.Overwrite, claimed.Contains);

                    targetPath = target.Path;
                    alreadyComplete = target.AlreadyComplete;

                    // --overwrite 分支不做认领检查，兜底再核一次，撞了就换随机名。
                    if (!claimed.Add(targetPath))
                    {
                        var stem = Path.GetFileNameWithoutExtension(targetPath);
                        var ext = Path.GetExtension(targetPath);
                        targetPath = Path.Combine(outputDir, $"{stem}_{Guid.NewGuid():N}{ext}");
                        claimed.Add(targetPath);
                        alreadyComplete = false;
                    }
                }

                task = ctx.AddTask(Markup.Escape(fileName));
                var reporter = new SpectreProgress(task, fileName, info.Length);

                if (alreadyComplete)
                {
                    counters.AddSkipped();
                    if (info.Length is { } totalLength && totalLength > 0)
                    {
                        task.MaxValue = totalLength;
                        task.Value = totalLength;
                    }
                    task.Description = $"[grey]已存在且完整，跳过：{Markup.Escape(fileName)}[/]";
                    log.Info($"跳过（已存在且完整）：{targetPath}");
                    return;
                }

                reporter.Begin();

                var downloader = new FileDownloader(http, config, limiter, new DownloadProgressSink
                {
                    OnBase = reporter.SetBase,
                    OnBytes = reporter.Add,
                    OnLog = log.Info,
                });

                await downloader.DownloadAsync(url, info, targetPath, cts.Token);

                var verificationError = await VerifyAsync(targetPath, info, settings, cts.Token);
                if (verificationError is not null)
                {
                    counters.AddFailed();
                    DeleteCorrupted(targetPath, log);
                    task.Description = $"[red]× 校验失败：{Markup.Escape(fileName)}[/]";
                    failures.Add($"{Shorten(url)} → {verificationError}");
                    log.Info($"校验失败：{targetPath}，{verificationError}");
                    return;
                }

                counters.AddCompleted();
                reporter.Complete();
                log.Info($"完成：{targetPath}（{new FileInfo(targetPath).Length} 字节）");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                counters.AddInterrupted();
                if (task is not null)
                    task.Description = $"[yellow]■ {Markup.Escape(displayName)} 已取消，续传状态已保存[/]";
                log.Info($"已取消：{url}");
            }
            catch (Exception ex)
            {
                counters.AddFailed();
                if (task is not null)
                    task.Description = $"[red]× {Markup.Escape(displayName)}：{Markup.Escape(ex.Message)}[/]";
                failures.Add($"{Shorten(url)} → {ex.Message}");
                log.Info($"失败：{url}，{ex.GetType().Name}: {ex.Message}");
            }
        }

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var slots = new SemaphoreSlim(config.MaxParallelFiles, config.MaxParallelFiles);

                var workers = urls.Select(async url =>
                {
                    try
                    {
                        await slots.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        counters.AddInterrupted();
                        return;
                    }

                    try
                    {
                        await DownloadOneAsync(ctx, url);
                    }
                    finally
                    {
                        slots.Release();
                    }
                });

                await Task.WhenAll(workers);
            });

        foreach (var note in failures)
            AnsiConsole.MarkupLine($"  [red]× 失败：[/]{Markup.Escape(note)}");
    }

    static void ApplyOverrides(DownloadConfig config, DownloadSettings settings)
    {
        if (settings.Connections is { } connections) config.MaxConnections = connections;
        if (settings.ParallelFiles is { } parallelFiles) config.MaxParallelFiles = parallelFiles;
        if (settings.Retries is { } retries) config.MaxRetries = retries;
        if (settings.IdleTimeoutSeconds is { } idle) config.ReadIdleTimeoutSeconds = idle;
        if (settings.NoResume) config.EnableResume = false;
        if (settings.ForceSingleThread) config.EnableMultiThread = false;
        config.Normalize();
    }

    static List<string> CollectUrls(DownloadSettings settings, RunLog log)
    {
        var urls = new List<string>();

        foreach (var url in settings.Urls ?? Array.Empty<string>())
        {
            if (IsHttpUrl(url)) urls.Add(url);
            else log.Info($"跳过非 URL 参数：{url}");
        }

        if (string.IsNullOrWhiteSpace(settings.ListFile)) return urls;

        if (!File.Exists(settings.ListFile))
        {
            log.Info($"找不到列表文件：{settings.ListFile}");
            return urls;
        }

        foreach (var line in File.ReadAllLines(settings.ListFile))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (IsHttpUrl(trimmed)) urls.Add(trimmed);
            else log.Info($"跳过列表中无效 URL：{trimmed}");
        }

        return urls;
    }

    static bool IsHttpUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>校验并去重镜像 URL（与主 URL 相同的忽略）。返回 null 表示参数组合非法（多文件 + 镜像）。</summary>
    static List<string>? CollectMirrorUrls(DownloadSettings settings, List<string> urls, RunLog log)
    {
        if (settings.Mirrors.Length == 0) return new List<string>();
        if (urls.Count != 1) return null;

        var result = new List<string>();
        foreach (var mirror in settings.Mirrors)
        {
            if (!IsHttpUrl(mirror))
            {
                log.Info($"跳过无效镜像 URL：{mirror}");
                continue;
            }

            if (mirror.Equals(urls[0], StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(m => m.Equals(mirror, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(mirror);
        }

        return result;
    }

    static HttpClient CreateHttpClient(DownloadConfig config, RequestContext context)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(config.ConnectTimeoutSeconds),
            // 重定向由 RequestPipelineHandler 手动跟随，才能记录完整链路并跨主机移除凭据。
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            // 并行多文件、单文件多镜像时同源并发上限按比例放大，否则连接池排队会把并行度吃掉。
            MaxConnectionsPerServer = Math.Max(2, config.MaxConnections * (config.MaxParallelFiles + 2) + 2),
        };

        if (config.ProxyEnabled && !string.IsNullOrWhiteSpace(config.ProxyAddress))
        {
            var proxy = new WebProxy(config.ProxyAddress);
            if (!string.IsNullOrWhiteSpace(config.ProxyUsername))
                proxy.Credentials = new NetworkCredential(config.ProxyUsername, config.ProxyPassword);
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        // Digest 认证需要 HttpClientHandler 参与 401 挑战；直接给凭据，让它自动重发。
        if (context.DigestCredential is { } digest)
            handler.Credentials = digest;

        // 不要设 HttpClient.Timeout：它会覆盖整个响应体读取，大文件必被砍断。
        // 超时由 ConnectTimeout + 每轮读取的空闲超时负责。
        var pipeline = new RequestPipelineHandler(context) { InnerHandler = handler };
        var client = new HttpClient(pipeline) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
        return client;
    }

    /// <summary>-o 指定精确路径时拆成目录+文件名；否则沿用按 Content-Disposition/URL 推断的名字。</summary>
    static (string Directory, string FileName) ResolveOutputName(
        DownloadSettings settings, string outputDir, RemoteFileInfo info, string url)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputPath))
            return (outputDir, FileNameResolver.Resolve(info, url));

        var full = Path.GetFullPath(settings.OutputPath);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
        Directory.CreateDirectory(dir);
        return (dir, Path.GetFileName(full));
    }

    /// <summary>--offline：按最终策略打印将要发送的请求，不发网络请求。</summary>
    static void PrintOfflineRequests(IReadOnlyList<string> urls, RequestContext context)
    {
        AnsiConsole.MarkupLine("[grey]离线模式：仅打印请求，不发起网络连接[/]");
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[blue]GET[/] {Markup.Escape(uri.ToString())} [grey]HTTP/1.1[/]");

            var printed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in context.HeadersFor(uri))
            {
                // 离线模式下用户就是想确认凭据有没有带对——这里显示原值，但绝不写日志。
                if (!printed.Add(name)) continue;
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(name)}:[/] {Markup.Escape(value)}");
            }
        }
        AnsiConsole.WriteLine();
    }

    static async Task<DownloadOutcome> DownloadWithProgressAsync(
        HttpClient http,
        DownloadConfig config,
        TokenBucketRateLimiter? limiter,
        DownloadSettings settings,
        RemoteFileInfo info,
        string url,
        string output,
        RunLog log,
        CancellationToken cancellationToken,
        IReadOnlyList<RemoteFileInfo>? mirrors = null)
    {
        var displayName = Path.GetFileName(output);

        return await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask(Markup.Escape(displayName));
                var reporter = new SpectreProgress(task, displayName, info.Length);
                reporter.Begin();

                var downloader = new FileDownloader(http, config, limiter, new DownloadProgressSink
                {
                    OnBase = reporter.SetBase,
                    OnBytes = reporter.Add,
                    OnLog = log.Info,
                });

                var outcome = await downloader.DownloadAsync(url, info, output, cancellationToken, mirrors);

                reporter.Complete();
                return outcome;
            });
    }

    /// <summary>返回 null 表示校验通过，否则返回失败原因。</summary>
    static async Task<string?> VerifyAsync(string path, RemoteFileInfo info, DownloadSettings settings, CancellationToken cancellationToken)
    {
        if (info.Length is > 0)
        {
            var actual = new FileInfo(path).Length;
            if (actual != info.Length)
                return $"文件长度不符：期望 {info.Length}，实际 {actual}";
        }

        if (string.IsNullOrWhiteSpace(settings.ExpectedSha256)) return null;

        var expected = settings.ExpectedSha256.Trim().Replace(" ", "").ToLowerInvariant();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hash).ToLowerInvariant();

        return string.Equals(actualHash, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SHA-256 不匹配：期望 {expected}，实际 {actualHash}";
    }

    /// <summary>
    /// 校验失败后删掉坏文件和残留状态。
    /// 必须删：完整长度的坏文件如果不删，下次会被当作"已完整"永久跳过，校验就成了摆设。
    /// 返回文件是否删除成功（删除失败时提示用户手动处理）。
    /// </summary>
    static bool DeleteCorrupted(string path, RunLog log)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            log.Info($"删除坏文件失败：{path}，{ex.Message}");
            AnsiConsole.MarkupLine($"  [yellow]! 坏文件删除失败，请手动删除：{Markup.Escape(path)}（{Markup.Escape(ex.Message)}）[/]");
            return false;
        }

        DownloadState.Delete(path);
        log.Info($"已删除校验失败的坏文件：{path}");
        return true;
    }

    static string Shorten(string url) => url.Length <= 96 ? url : url[..93] + "…";
}

/// <summary>把下载进度映射到 Spectre 的进度条上，并对刷新频率做节流。</summary>
public sealed class SpectreProgress
{
    static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(120);

    readonly ProgressTask _task;
    readonly string _displayName;
    readonly SpeedMeter _meter = new();
    readonly object _gate = new();

    long _downloaded;
    long _total;
    long _lastRenderTimestamp;

    public SpectreProgress(ProgressTask task, string displayName, long? total)
    {
        _task = task;
        _displayName = displayName;
        _total = total ?? 0;

        if (_total > 0)
            _task.MaxValue = _total;
        else
            _task.IsIndeterminate = true;
    }

    public void Begin()
    {
        lock (_gate)
        {
            _task.Value = 0;
            _task.Description = $"[green]{Markup.Escape(_displayName)}[/] [grey]准备中[/]";
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public void SetBase(long bytes)
    {
        Interlocked.Exchange(ref _downloaded, bytes);
        lock (_gate)
        {
            if (_total > 0) _task.Value = Math.Min(bytes, _total);
        }
    }

    public void Add(long bytes)
    {
        Interlocked.Add(ref _downloaded, bytes);
        _meter.Add(bytes);
        Render(force: false);
    }

    public void Complete()
    {
        Render(force: true, completed: true);
    }

    void Render(bool force, bool completed = false)
    {
        var now = Stopwatch.GetTimestamp();
        var sinceLast = (now - _lastRenderTimestamp) / (double)Stopwatch.Frequency;
        if (!force && sinceLast < RenderInterval.TotalSeconds) return;

        lock (_gate)
        {
            sinceLast = (now - _lastRenderTimestamp) / (double)Stopwatch.Frequency;
            if (!force && sinceLast < RenderInterval.TotalSeconds) return;
            _lastRenderTimestamp = now;

            var downloaded = Interlocked.Read(ref _downloaded);

            if (_total > 0)
            {
                _task.Value = Math.Min(downloaded, _total);
                var speed = _meter.BytesPerSecond;
                var eta = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _total - downloaded) / (double)speed) : TimeSpan.Zero;

                var tail = completed
                    ? "[green]完成[/]"
                    : speed > 0
                        ? $"[blue]{SizeFormatter.Format(speed)}/s[/] [grey]剩余 {SizeFormatter.FormatDuration(eta)}[/]"
                        : "[grey]连接中[/]";

                _task.Description =
                    $"[green]{Markup.Escape(_displayName)}[/] " +
                    $"[yellow]{SizeFormatter.Format(downloaded)}/{SizeFormatter.Format(_total)}[/] " +
                    tail;
            }
            else
            {
                var speed = _meter.BytesPerSecond;
                _task.Description =
                    $"[green]{Markup.Escape(_displayName)}[/] " +
                    $"[yellow]{SizeFormatter.Format(downloaded)}[/] " +
                    (completed ? "[green]完成[/]" : $"[blue]{SizeFormatter.Format(speed)}/s[/]");
            }
        }
    }
}

/// <summary>整批下载的计数。并行下载时会被多个任务同时递增，所以走 Interlocked。</summary>
public sealed class DownloadCounters
{
    long _completed;
    long _skipped;
    long _failed;
    long _interrupted;

    public long Completed => Interlocked.Read(ref _completed);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Failed => Interlocked.Read(ref _failed);
    public long Interrupted => Interlocked.Read(ref _interrupted);

    public void AddCompleted() => Interlocked.Increment(ref _completed);
    public void AddSkipped() => Interlocked.Increment(ref _skipped);
    public void AddFailed() => Interlocked.Increment(ref _failed);
    public void AddInterrupted() => Interlocked.Increment(ref _interrupted);
}

/// <summary>简单运行日志：写文件，不干扰进度条渲染。</summary>
public sealed class RunLog
{
    readonly string? _path;

    public RunLog(string? path) => _path = path;

    public void Info(string message)
    {
        if (string.IsNullOrEmpty(_path)) return;

        try
        {
            File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不影响下载
        }
    }
}
