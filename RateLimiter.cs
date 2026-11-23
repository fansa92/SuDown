using System.Diagnostics;

/// <summary>
/// 全局令牌桶限速器。一个进程只建一个实例：所有分片、并行下载的多个文件都从这里取令牌，
/// 所以 -s 100000 是真的全局 100 KB/s，而不是每连接/每文件各 100 KB/s。
/// </summary>
public sealed class TokenBucketRateLimiter
{
    readonly long _rate;
    readonly double _capacity;
    readonly object _gate = new();

    double _tokens;
    long _lastTimestamp;

    public TokenBucketRateLimiter(long bytesPerSecond)
    {
        _rate = bytesPerSecond;
        // 桶容量至少要能装下一次缓冲区读取，否则小限速值会永远等不到足够令牌。
        _capacity = Math.Max(bytesPerSecond, 256 * 1024);
        _tokens = _capacity;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public async ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (_rate <= 0 || bytes <= 0) return;

        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Refill();
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds((bytes - _tokens) / (double)_rate);
            }

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
        }
    }

    void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        if (seconds <= 0) return;
        _tokens = Math.Min(_capacity, _tokens + seconds * _rate);
    }
}

/// <summary>
/// 滑动窗口速度计。只统计最近几秒的流量，所以能立刻反映速度变化，
/// 而不是像累计平均那样"掉速了还显示高速"。
/// </summary>
public sealed class SpeedMeter
{
    const double WindowSeconds = 3.0;
    const int MaxSamples = 512;

    readonly Queue<(long Timestamp, long Total)> _samples = new();
    long _total;

    public SpeedMeter()
    {
        _samples.Enqueue((Stopwatch.GetTimestamp(), 0));
    }

    public void Add(long bytes)
    {
        if (bytes <= 0) return;

        lock (_samples)
        {
            _total += bytes;
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, _total));

            while (_samples.Count > 2)
            {
                var oldest = _samples.Peek();
                if ((now - oldest.Timestamp) / (double)Stopwatch.Frequency > WindowSeconds || _samples.Count > MaxSamples)
                    _samples.Dequeue();
                else
                    break;
            }
        }
    }

    public long BytesPerSecond
    {
        get
        {
            lock (_samples)
            {
                if (_samples.Count < 2) return 0;
                var first = _samples.Peek();
                var last = _samples.Last();
                var seconds = (last.Timestamp - first.Timestamp) / (double)Stopwatch.Frequency;
                if (seconds < 0.2) return 0;
                return (long)((last.Total - first.Total) / seconds);
            }
        }
    }
}

public static class SizeFormatter
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        if (bytes < 0) return "-";
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {Units[unit]}";
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}
