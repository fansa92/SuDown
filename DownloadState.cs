using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>单个分片的进度。</summary>
public sealed class ChunkState
{
    long _done;

    /// <summary>分片在文件中的起始偏移。</summary>
    public long Start { get; set; }

    /// <summary>分片长度（字节）。</summary>
    public long Length { get; set; }

    /// <summary>已写入该分片的字节数。多线程会并发更新，所以走 Interlocked。</summary>
    public long Done
    {
        get => Interlocked.Read(ref _done);
        set => Interlocked.Exchange(ref _done, value);
    }

    public void AddProgress(long bytes) => Interlocked.Add(ref _done, bytes);

    [JsonIgnore]
    public bool IsComplete => Length > 0 && Done >= Length;

    [JsonIgnore]
    public long Remaining => Math.Max(0, Length - Done);
}

/// <summary>
/// 下载状态文件（同目录下的 &lt;文件名&gt;.sudown）。
/// 它存在的唯一目的：区分"文件长度恰好等于总长"和"文件真的下载完了"。
/// 每次下载开始前先落地状态文件、再预分配文件，任何时刻被杀掉都能接着下。
/// </summary>
public sealed class DownloadState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Url { get; set; } = "";
    public long TotalBytes { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<ChunkState> Chunks { get; set; } = new();

    [JsonIgnore]
    public bool IsComplete => Chunks.Count > 0 && Chunks.All(c => c.IsComplete);

    [JsonIgnore]
    public long DownloadedBytes => Chunks.Sum(c => Math.Min(c.Done, c.Length));

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetPath(string outputPath) => outputPath + ".sudown";

    /// <summary>原子写入：先写临时文件再替换，避免写到一半崩溃留下坏状态。</summary>
    public void Save(string outputPath)
    {
        var path = GetPath(outputPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static DownloadState? TryLoad(string outputPath)
    {
        var path = GetPath(outputPath);
        if (!File.Exists(path)) return null;

        try
        {
            var state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path), JsonOptions);
            if (state is null || state.Version != CurrentVersion) return null;
            if (state.Chunks.Count == 0) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string outputPath)
    {
        try
        {
            var path = GetPath(outputPath);
            if (File.Exists(path)) File.Delete(path);
            // 强杀可能恰好卡在"tmp 已写、还没改名"的窗口里，把残留的 tmp 一起清掉。
            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch { /* 状态文件删不掉不影响主流程 */ }
    }

    /// <summary>
    /// 判断旧状态是否还能用：远端文件变了（ETag / Last-Modified / 总长任一不同）就不能拿来续传，
    /// 否则会把两个版本的文件拼成一个四不像。
    /// </summary>
    public bool Matches(long totalBytes, string? etag, DateTimeOffset? lastModified)
    {
        if (TotalBytes != totalBytes) return false;

        if (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(etag))
            return string.Equals(ETag, etag, StringComparison.Ordinal);

        if (LastModified.HasValue && lastModified.HasValue)
            return LastModified.Value == lastModified.Value;

        // 服务器两种校验头都没给，只能信长度。这种情况下续传风险由调用方决定。
        return true;
    }
}
