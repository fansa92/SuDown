using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Polly;

public enum DownloadOutcome
{
    /// <summary>真的下载完成并校验通过。</summary>
    Completed,
    /// <summary>目标文件本来就完整，跳过。</summary>
    AlreadyComplete,
}

/// <summary>下载过程中的进度与日志回调。UI 层用它更新进度条。</summary>
public sealed class DownloadProgressSink
{
    /// <summary>本次新增的字节数（增量，可跨线程调用）。</summary>
    public Action<long>? OnBytes { get; init; }

    /// <summary>续传起点：告诉 UI 已经有多少字节是之前下好的。</summary>
    public Action<long>? OnBase { get; init; }

    /// <summary>过程日志，只写文件不要写控制台（会破坏进度条渲染）。</summary>
    public Action<string>? OnLog { get; init; }
}

/// <summary>
/// 下载引擎。负责分片调度、镜像源容错、续传状态、限速、重试、完整性校验。
/// </summary>
public sealed class FileDownloader
{
    const int BufferSize = 64 * 1024;

    /// <summary>每写入这么多字节落一次状态文件，兼顾磁盘 IO 与中断损失。</summary>
    const long StateCheckpointBytes = 4 * 1024 * 1024;

    /// <summary>即使没写满一个检查点，也每秒落一次盘，保证被强杀时最多只丢 1 秒进度。</summary>
    static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 分片粒度：目标片数 ≈ 连接数 × ChunksPerConnection，单片最小 512KB、最大 16MB。
    /// 片够小，慢连接最多拖住自己手里的一片，快连接做完立刻认领下一片；
    /// 片太大则退化为旧版的"一个连接扛到底"，尾部被最慢的连接拖住。
    /// </summary>
    const long MinChunkSize = 512 * 1024;
    const long MaxChunkSize = 16 * 1024 * 1024;
    const int ChunksPerConnection = 4;

    readonly HttpClient _http;
    readonly DownloadConfig _config;
    readonly TokenBucketRateLimiter? _limiter;
    readonly DownloadProgressSink _progress;
    readonly object _stateLock = new();

    long _downloaded;
    long _unsavedBytes;
    bool _checkpointStopped;

    /// <param name="limiter">限速桶，可为 null（不限速）。跨下载共享同一个实例即为全局限速。</param>
    public FileDownloader(HttpClient http, DownloadConfig config, TokenBucketRateLimiter? limiter, DownloadProgressSink progress)
    {
        _http = http;
        _config = config;
        _limiter = limiter;
        _progress = progress;
    }

    public long DownloadedBytes => Interlocked.Read(ref _downloaded);

    /// <param name="mirrors">镜像源。仅当主源支持分段且长度已知时参与分片下载；可为 null 或空。</param>
    public async Task<DownloadOutcome> DownloadAsync(string url, RemoteFileInfo info, string output, CancellationToken cancellationToken, IReadOnlyList<RemoteFileInfo>? mirrors = null)
    {
        var total = info.Length ?? 0;

        // 拿不到总长、或服务器不支持 Range：只能顺序单流写，无法分片也无法续传。
        if (total <= 0)
            return await DownloadUnknownLengthAsync(info, output, cancellationToken);

        var sourceInfos = new List<RemoteFileInfo> { info };
        if (info.SupportsRanges && mirrors is { Count: > 0 })
            sourceInfos.AddRange(mirrors);

        var sources = sourceInfos.Select(i => new DownloadSource(i)).ToList();
        var connections = ComputeConnections(info, total, sources.Count);
        var totalWorkers = connections * sources.Count;
        var state = LoadOrCreateState(url, info, output, total, totalWorkers);

        if (state.IsComplete)
        {
            DownloadState.Delete(output);
            return DownloadOutcome.AlreadyComplete;
        }

        // 顺序很重要：状态文件已经落地，再预分配。这样即使在这一步被 kill，
        // 下次也能凭状态文件知道"这些空洞还没填"。
        using (var preallocate = new FileStream(output, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            preallocate.SetLength(total);

        Interlocked.Exchange(ref _downloaded, state.DownloadedBytes);
        _progress.OnBase?.Invoke(state.DownloadedBytes);

        var pending = state.Chunks.Where(c => !c.IsComplete).ToList();
        _progress.OnLog?.Invoke(sources.Count > 1
            ? $"开始下载：{sources.Count} 个镜像源 × {connections} 连接（共 {totalWorkers} 连接），{pending.Count} 个分片待完成，总长 {SizeFormatter.Format(total)}"
            : $"开始下载：{totalWorkers} 个连接，{pending.Count} 个分片待完成，总长 {SizeFormatter.Format(total)}");

        using var checkpointTimer = new Timer(_ => PersistIfDirty(state, output), null, CheckpointInterval, CheckpointInterval);

        try
        {
            // 分片放进共享队列，固定数量的 worker 谁空谁认领下一片。
            // 慢连接最多拖住自己手里的一片，快连接做完立刻拿新片，尾部不再被拖死。
            // worker 按 index 轮转绑定镜像源，连接均匀分摊到各源；源挂了由 failover 兜底。
            var queue = new ConcurrentQueue<ChunkState>(pending);

            await Task.WhenAll(Enumerable.Range(0, totalWorkers)
                .Select(w => WorkerLoopAsync(state, output, queue, sources, w, cancellationToken)));
        }
        catch
        {
            checkpointTimer.Dispose();
            SaveState(state, output);
            throw;
        }

        checkpointTimer.Dispose();

        if (!state.IsComplete)
            throw new IOException("下载结束后仍有未完成的分片");

        var finalLength = new FileInfo(output).Length;
        if (finalLength != total)
            throw new IOException($"文件长度校验失败：期望 {total} 字节，实际 {finalLength} 字节");

        FinishAndDeleteState(output);
        _progress.OnLog?.Invoke($"下载并校验完成：{output}");
        return DownloadOutcome.Completed;
    }

    /// <summary>
    /// 每个镜像源的连接数：-c 是总连接预算，在源之间均分（每源至少 1 个）。
    /// 单源时和原来完全一致。
    /// </summary>
    int ComputeConnections(RemoteFileInfo info, long total, int sourceCount)
    {
        if (!_config.EnableMultiThread) return 1;
        if (!info.SupportsRanges) return 1;
        if (total <= _config.MinSizeForMultiThread) return 1;

        var bySize = total / _config.MinSizeForMultiThread;
        var budget = (int)Math.Min(_config.MaxConnections, Math.Max(1, bySize));
        budget = Math.Clamp(budget, 1, 32);
        return Math.Max(1, budget / sourceCount);
    }

    /// <summary>目标片数约为连接数的 4 倍：片太小请求开销占比高，片太大慢连接拖尾变久。</summary>
    static long ComputeChunkSize(long total, int connections)
        => Math.Clamp(total / (connections * ChunksPerConnection), MinChunkSize, MaxChunkSize);

    async Task WorkerLoopAsync(DownloadState state, string output, ConcurrentQueue<ChunkState> queue, List<DownloadSource> sources, int workerIndex, CancellationToken cancellationToken)
    {
        while (queue.TryDequeue(out var chunk))
            await DownloadChunkWithFailoverAsync(state, output, chunk, sources, workerIndex, cancellationToken);
    }

    /// <summary>从 preferred 开始轮转找第一个健康源，全部被排除时返回 -1。</summary>
    static int PickHealthyIndex(List<DownloadSource> sources, int preferred)
    {
        for (var k = 0; k < sources.Count; k++)
        {
            var i = (preferred + k) % sources.Count;
            if (!sources[i].Disabled) return i;
        }
        return -1;
    }

    async Task DownloadChunkWithFailoverAsync(DownloadState state, string output, ChunkState chunk, List<DownloadSource> sources, int preferredIndex, CancellationToken cancellationToken)
    {
        var index = PickHealthyIndex(sources, preferredIndex);
        if (index < 0)
            throw new PermanentDownloadException("所有镜像源均不可用，无法继续下载");

        while (true)
        {
            var source = sources[index];
            try
            {
                await DownloadChunkOnSourceAsync(state, source.Info, output, chunk, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单源失败不连坐整个下载：排除该源，分片（含半片进度）换健康源续上。
                // 每轮排除一个源，循环必然终止。
                source.Disabled = true;
                _progress.OnLog?.Invoke($"镜像源不可用，已排除：{source.Info.FinalUri}（{ex.Message}）");

                var next = PickHealthyIndex(sources, index + 1);
                if (next < 0)
                {
                    if (sources.Count == 1) throw;
                    throw new PermanentDownloadException($"所有镜像源均不可用，最后错误：{ex.Message}");
                }

                index = next;
            }
        }
    }

    DownloadState LoadOrCreateState(string url, RemoteFileInfo info, string output, long total, int totalWorkers)
    {
        var existing = _config.EnableResume ? DownloadState.TryLoad(output) : null;

        if (existing is not null)
        {
            if (existing.Url == url && existing.Matches(total, info.ETag, info.LastModified))
            {
                _progress.OnLog?.Invoke($"发现续传状态：已完成 {SizeFormatter.Format(existing.DownloadedBytes)}/{SizeFormatter.Format(total)}");
                return existing;
            }

            _progress.OnLog?.Invoke("远端文件已变化（长度或校验头不同），丢弃旧续传状态重新下载");
            DownloadState.Delete(output);
        }

        var state = new DownloadState
        {
            Url = url,
            TotalBytes = total,
            ETag = info.ETag,
            LastModified = info.LastModified,
        };

        if (totalWorkers == 1)
        {
            // 单线程：旧版本下载到一半的文件，可以按现有长度续传。
            var resumeFrom = 0L;
            if (_config.EnableResume && File.Exists(output))
            {
                var length = new FileInfo(output).Length;
                if (length > 0 && length < total) resumeFrom = length;
            }

            state.Chunks.Add(new ChunkState { Start = 0, Length = total, Done = resumeFrom });
            if (resumeFrom > 0)
                _progress.OnLog?.Invoke($"从 {SizeFormatter.Format(resumeFrom)} 处续传（无历史状态文件）");
        }
        else
        {
            // 多连接：切成比连接数更细的小片，worker 从队列认领，天然负载均衡。
            var chunkSize = ComputeChunkSize(total, totalWorkers);
            for (var start = 0L; start < total; start += chunkSize)
            {
                var length = Math.Min(chunkSize, total - start);
                state.Chunks.Add(new ChunkState { Start = start, Length = length });
            }
        }

        SaveState(state, output);
        return state;
    }

    async Task DownloadChunkOnSourceAsync(DownloadState state, RemoteFileInfo info, string output, ChunkState chunk, CancellationToken cancellationToken)
    {
        var retry = Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                _config.MaxRetries,
                attempt => TimeSpan.FromSeconds(_config.RetryDelaySeconds * Math.Pow(2, attempt - 1)),
                (exception, delay, attempt, _) =>
                    _progress.OnLog?.Invoke($"分片 @{chunk.Start} 第 {attempt} 次重试（{delay.TotalSeconds:0}s 后）：{exception.Message}"));

        await retry.ExecuteAsync(async token => await TransferChunkAsync(state, info, output, chunk, token), cancellationToken);
    }

    async Task TransferChunkAsync(DownloadState state, RemoteFileInfo info, string output, ChunkState chunk, CancellationToken cancellationToken)
    {
        var from = chunk.Start + chunk.Done;
        var expectedEnd = chunk.Start + chunk.Length;
        var to = expectedEnd - 1;
        if (from > to) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, info.FinalUri);
        request.Headers.Range = new RangeHeaderValue(from, to);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            // 确认服务器真的从我们要的位置开始返回，否则写进去就是错位数据。
            if (response.Content.Headers.ContentRange?.From is long actualFrom && actualFrom != from)
                throw new InvalidDataException($"服务器返回的区间起点不符：期望 {from}，实际 {actualFrom}");
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // 服务器无视 Range 直接给了整文件。只有"单片 + 从头下"才能接受。
            if (from != 0 || chunk.Length != state.TotalBytes)
                throw new PermanentDownloadException("服务器忽略了 Range 请求，无法分片下载");
        }
        else
        {
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PermanentDownloadException($"服务器返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // bufferSize: 1 = 禁用 FileStream 自己的用户态缓冲。
        // 必须这么做：进度是按"写入调用返回"累加的，如果数据还压在 FileStream 缓冲里
        // 进程就被杀掉，状态文件会声称写过、实际文件是空洞，续传就拼不出正确的文件。
        // 禁用缓冲后每次写入直接进操作系统页缓存，进程死亡也不会丢。
        await using var file = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
        file.Position = from;

        var buffer = new byte[BufferSize];
        var position = from;
        var sinceCheckpoint = 0L;

        // 复用同一个 CTS，每轮只重置计时器，避免每读 64KB 就新建一个 CancellationTokenSource。
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idle = TimeSpan.FromSeconds(_config.ReadIdleTimeoutSeconds);

        while (position < expectedEnd)
        {
            var want = (int)Math.Min(buffer.Length, expectedEnd - position);

            idleTimeout.CancelAfter(idle);

            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, want), idleTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连续 {_config.ReadIdleTimeoutSeconds} 秒没有收到数据");
            }

            if (read <= 0) break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            position += read;
            chunk.AddProgress(read);
            sinceCheckpoint += read;

            ReportBytes(read);

            if (_limiter is not null)
                await _limiter.AcquireAsync(read, cancellationToken);

            if (sinceCheckpoint >= StateCheckpointBytes)
            {
                sinceCheckpoint = 0;
                SaveState(state, output);
            }
        }

        await file.FlushAsync(cancellationToken);

        if (position < expectedEnd)
            throw new IOException($"分片提前结束：期望写到 {expectedEnd}，实际 {position}");
    }

    async Task<DownloadOutcome> DownloadUnknownLengthAsync(RemoteFileInfo info, string output, CancellationToken cancellationToken)
    {
        var retry = Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                _config.MaxRetries,
                attempt => TimeSpan.FromSeconds(_config.RetryDelaySeconds * Math.Pow(2, attempt - 1)),
                (exception, delay, attempt, _) =>
                    _progress.OnLog?.Invoke($"第 {attempt} 次重试（{delay.TotalSeconds:0}s 后）：{exception.Message}"));

        _progress.OnLog?.Invoke("无法获取文件大小，使用单流顺序下载（不支持续传）");

        await retry.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.FinalUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PermanentDownloadException($"服务器返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            await using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);

            var buffer = new byte[BufferSize];
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var idle = TimeSpan.FromSeconds(_config.ReadIdleTimeoutSeconds);

            while (true)
            {
                idleTimeout.CancelAfter(idle);

                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, idleTimeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException($"连续 {_config.ReadIdleTimeoutSeconds} 秒没有收到数据");
                }

                if (read <= 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), token);
                ReportBytes(read);

                if (_limiter is not null)
                    await _limiter.AcquireAsync(read, token);
            }

            await file.FlushAsync(token);
        }, cancellationToken);

        return DownloadOutcome.Completed;
    }

    void ReportBytes(long bytes)
    {
        Interlocked.Add(ref _downloaded, bytes);
        Interlocked.Add(ref _unsavedBytes, bytes);
        _progress.OnBytes?.Invoke(bytes);
    }

    /// <summary>定时落盘：只要还有没保存的进度就写一次，被强杀时最多丢一个检查点间隔。</summary>
    void PersistIfDirty(DownloadState state, string output)
    {
        if (Interlocked.Read(ref _unsavedBytes) <= 0) return;

        lock (_stateLock)
        {
            if (_checkpointStopped) return;
            if (Interlocked.Exchange(ref _unsavedBytes, 0) <= 0) return;

            try
            {
                state.Save(output);
            }
            catch (Exception ex)
            {
                _progress.OnLog?.Invoke($"保存续传状态失败：{ex.Message}");
            }
        }
    }

    void SaveState(DownloadState state, string output)
    {
        lock (_stateLock)
        {
            if (_checkpointStopped) return;

            try
            {
                state.Save(output);
                Interlocked.Exchange(ref _unsavedBytes, 0);
            }
            catch (Exception ex)
            {
                _progress.OnLog?.Invoke($"保存续传状态失败：{ex.Message}");
            }
        }
    }

    /// <summary>下载真正完成才删状态文件。先锁住，避免定时器回调把文件又写回来。</summary>
    void FinishAndDeleteState(string output)
    {
        lock (_stateLock)
        {
            _checkpointStopped = true;
            DownloadState.Delete(output);
        }
    }

    /// <summary>只有临时性故障才重试。4xx、认证失败、取消都不该重试。</summary>
    static bool IsTransient(Exception exception) => exception switch
    {
        PermanentDownloadException => false,
        OperationCanceledException => false,
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException http => (int)http.StatusCode! is >= 500 or 408 or 429,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ => false,
    };
}

/// <summary>一个下载源（主 URL 或镜像）。Disabled 表示已证实不可靠（连不上、4xx、区间不符），后续分片不再选它。</summary>
sealed class DownloadSource
{
    public DownloadSource(RemoteFileInfo info) => Info = info;

    public RemoteFileInfo Info { get; }

    public bool Disabled { get; set; }
}
