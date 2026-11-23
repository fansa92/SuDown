using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

/// <summary>探测到的远端文件信息。</summary>
public sealed class RemoteFileInfo
{
    public Uri FinalUri { get; init; } = new("about:blank");
    public long? Length { get; init; }
    public bool SupportsRanges { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ContentDisposition { get; init; }
    public string? ContentType { get; init; }
    public HttpStatusCode StatusCode { get; init; }
}

/// <summary>
/// 远端探测。先 HEAD，失败（很多 CDN 对 HEAD 返回 405/403）或拿不到长度时，
/// 回退到 GET + Range: bytes=0-0 读 Content-Range，而不是直接中断下载。
/// </summary>
public static class RemoteProbe
{
    public static async Task<RemoteFileInfo> ProbeAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headResponse = await http.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResponse.IsSuccessStatusCode)
            {
                var length = headResponse.Content.Headers.ContentLength;
                if (length is > 0)
                    return Build(uri, headResponse, length, SupportsRanges(headResponse));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // HEAD 不通，走下面的 Range 探测
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using var rangeResponse = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!rangeResponse.IsSuccessStatusCode)
        {
            if ((int)rangeResponse.StatusCode is >= 400 and < 500)
                throw new PermanentDownloadException($"服务器拒绝了请求（HTTP {(int)rangeResponse.StatusCode} {rangeResponse.ReasonPhrase}）");
            rangeResponse.EnsureSuccessStatusCode();
        }

        var isPartial = rangeResponse.StatusCode == HttpStatusCode.PartialContent;
        long? total = isPartial
            ? rangeResponse.Content.Headers.ContentRange?.Length
            : rangeResponse.Content.Headers.ContentLength;

        return Build(uri, rangeResponse, total, isPartial);
    }

    static RemoteFileInfo Build(Uri uri, HttpResponseMessage response, long? length, bool supportsRanges)
        => new()
        {
            FinalUri = response.RequestMessage?.RequestUri ?? uri,
            Length = length is > 0 ? length : null,
            SupportsRanges = supportsRanges,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified,
            ContentDisposition = response.Content.Headers.ContentDisposition?.ToString()
                                  ?? (response.Content.Headers.TryGetValues("Content-Disposition", out var cd) ? string.Join(";", cd) : null),
            ContentType = response.Content.Headers.ContentType?.MediaType,
            StatusCode = response.StatusCode,
        };

    static bool SupportsRanges(HttpResponseMessage response)
        => response.Headers.TryGetValues("Accept-Ranges", out var values)
           && values.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 文件名解析。依次尝试 Content-Disposition（含 RFC 5987 的 filename*）、URL 末段、
/// 再由 Content-Type 推断扩展名，最后清洗非法字符。
/// </summary>
public static class FileNameResolver
{
    const int MaxLength = 150;

    static readonly Dictionary<string, string> ContentTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/pdf"] = ".pdf",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["application/octet-stream"] = ".bin",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/x-msdownload"] = ".exe",
        ["application/x-7z-compressed"] = ".7z",
        ["application/x-rar-compressed"] = ".rar",
        ["application/gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["video/webm"] = ".webm",
        ["audio/mpeg"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["text/csv"] = ".csv",
    };

    public static string Resolve(RemoteFileInfo info, string url)
    {
        var name = FromContentDisposition(info.ContentDisposition);

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var path = new Uri(url).LocalPath;
                name = Uri.UnescapeDataString(Path.GetFileName(path));
            }
            catch
            {
                name = "";
            }
        }

        name = Sanitize(name ?? "");

        if (string.IsNullOrEmpty(name)) name = "download";

        if (!Path.HasExtension(name) && info.ContentType is { } ct
            && ContentTypeExtensions.TryGetValue(ct, out var ext))
            name += ext;

        return name;
    }

    static string? FromContentDisposition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // RFC 5987: filename*=UTF-8''%E4%B8%AD%E6%96%87.zip
        var extended = Regex.Match(raw, @"filename\*\s*=\s*([^']*)''([^;]+)", RegexOptions.IgnoreCase);
        if (extended.Success)
        {
            try
            {
                return Uri.UnescapeDataString(extended.Groups[2].Value.Trim().Trim('"'));
            }
            catch
            {
                // 解码失败就退回普通 filename
            }
        }

        var plain = Regex.Match(raw, @"filename\s*=\s*(""[^""]*""|[^;]+)", RegexOptions.IgnoreCase);
        if (plain.Success)
            return plain.Groups[1].Value.Trim().Trim('"');

        return null;
    }

    static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.', ' ');

        // Windows 保留名
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var ext = Path.GetExtension(cleaned);
            var keep = MaxLength - ext.Length;
            if (keep < 8) keep = MaxLength;
            cleaned = cleaned[..Math.Min(keep, cleaned.Length)] + ext;
        }

        return cleaned;
    }

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}

/// <summary>
/// 输出路径决策：续传复用原文件、已完整则跳过、同名冲突则加 (1)(2) 后缀。
/// 旧实现里两个不同 URL 会互相覆盖成一个文件，这里修掉。
/// </summary>
public static class FileTarget
{
    public sealed record Resolved(string Path, bool AlreadyComplete);

    /// <param name="isClaimed">
    /// 并行下载时由调用方提供的"这批任务里已有谁认领了这个路径"检查。
    /// 磁盘看不出两个并行任务同时解析同一个名字的差别，不加这个检查两个连接会写坏同一个文件。
    /// </param>
    public static Resolved Resolve(string directory, string fileName, long? totalBytes, bool supportsRanges, bool enableResume, bool overwrite = false, Func<string, bool>? isClaimed = null)
    {
        var candidate = System.IO.Path.Combine(directory, fileName);

        // --overwrite：现有文件和续传状态一律作废，从头下载并落回原文件名。
        // 不删状态文件的话，旧分片进度会混进新下载里。
        if (overwrite)
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            DownloadState.Delete(candidate);
            return new Resolved(candidate, false);
        }

        var state = enableResume ? DownloadState.TryLoad(candidate) : null;

        // 有匹配的状态 → 续传同一个文件；但被同批任务认领的绝不共用
        if (state is not null && totalBytes is > 0 && state.TotalBytes == totalBytes && isClaimed?.Invoke(candidate) != true)
            return new Resolved(candidate, false);

        if (!File.Exists(candidate) && !File.Exists(DownloadState.GetPath(candidate)) && isClaimed?.Invoke(candidate) != true)
            return new Resolved(candidate, false);

        if (File.Exists(candidate) && totalBytes is > 0 && isClaimed?.Invoke(candidate) != true)
        {
            var length = new FileInfo(candidate).Length;

            if (length == totalBytes && state is null)
                return new Resolved(candidate, true);
        }

        // 注意：没有状态文件就绝不拿现有文件续传。文件可能是别的 URL 下下来的同名文件，
        // 按长度猜续传会把两个不同文件拼成一个四不像。宁可加序号重新下。
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(stem)) stem = "download";

        for (var i = 1; i < 1000; i++)
        {
            var p = System.IO.Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !File.Exists(DownloadState.GetPath(p)) && isClaimed?.Invoke(p) != true)
                return new Resolved(p, false);
        }

        return new Resolved(System.IO.Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}"), false);
    }
}

/// <summary>不可重试的错误（4xx、服务器不支持 Range 等）。</summary>
public sealed class PermanentDownloadException : Exception
{
    public PermanentDownloadException(string message) : base(message) { }
}

/// <summary>
/// 镜像源解析：逐个探测 -m 传入的镜像，和主源比对长度与 ETag。
/// 只有"内容一致且支持分段"的源才允许参与；宁可单源慢一点，也不把不同内容拼进同一个文件。
/// </summary>
public static class MirrorResolver
{
    /// <summary>返回通过校验、可参与分片下载的镜像源列表（可能为空）。</summary>
    public static async Task<List<RemoteFileInfo>> ResolveAsync(
        HttpClient http, RemoteFileInfo primary, IReadOnlyList<string> mirrorUrls, Action<string> log, CancellationToken cancellationToken)
    {
        if (mirrorUrls.Count == 0)
            return new List<RemoteFileInfo>();

        if (primary.Length is not > 0 || !primary.SupportsRanges)
        {
            log("主源长度未知或不支持分段，镜像无法参与，按单源下载");
            return new List<RemoteFileInfo>();
        }

        var probes = await Task.WhenAll(mirrorUrls.Select(u => ProbeOne(http, u, cancellationToken)));

        var mirrors = new List<RemoteFileInfo>();
        foreach (var probe in probes)
        {
            if (probe.Info is null)
            {
                log($"镜像探测失败，已排除：{probe.Url}（{probe.Error}）");
                continue;
            }

            if (probe.Info.Length != primary.Length)
            {
                log($"镜像长度不符（期望 {primary.Length} 字节，实际 {probe.Info.Length} 字节），已排除：{probe.Url}");
                continue;
            }

            if (!probe.Info.SupportsRanges)
            {
                log($"镜像不支持分段下载，已排除：{probe.Url}");
                continue;
            }

            // ETag 只在两边都有时才比对：缺校验头时退回"长度一致即信任"，与续传的宽松策略一致。
            if (!string.IsNullOrEmpty(primary.ETag) && !string.IsNullOrEmpty(probe.Info.ETag)
                && !string.Equals(primary.ETag, probe.Info.ETag, StringComparison.Ordinal))
            {
                log($"镜像内容不一致（ETag 不同），已排除：{probe.Url}");
                continue;
            }

            mirrors.Add(probe.Info);
        }

        if (mirrors.Count > 0)
            log($"可用镜像源：{mirrors.Count + 1} 个（含主源）");

        return mirrors;
    }

    static async Task<(string Url, RemoteFileInfo? Info, string? Error)> ProbeOne(HttpClient http, string mirrorUrl, CancellationToken cancellationToken)
    {
        try
        {
            return (mirrorUrl, await RemoteProbe.ProbeAsync(http, mirrorUrl, cancellationToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (mirrorUrl, null, ex.Message);
        }
    }
}
