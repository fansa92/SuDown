using System.Net;

/// <summary>
/// 挂在 HttpClient 上的委托处理器：给出站请求注入头/认证/Cookie，收下 Set-Cookie，
/// 手动跟随重定向以记录完整链路，并在 -v 下打印请求与响应。
///
/// 放在这一层而不是改 RemoteProbe / FileDownloader，是为了让 HEAD 探测、Range 探测、
/// 镜像校验、分片下载、未知长度下载全部自动生效——漏掉任何一处都会导致"探测能过、下载 403"。
/// </summary>
public sealed class RequestPipelineHandler : DelegatingHandler
{
    readonly RequestContext _context;

    public RequestPipelineHandler(RequestContext context) => _context = context;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = Clone(request);
        ApplyHeaders(current);

        if (_context.Verbose)
            LogRequest(current);

        // 内层 handler 关掉自动重定向，这里手动跟，才能拿到中途每一跳。
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, cancellationToken);

            CollectCookies(response);
            if (_context.Verbose) LogResponse(response);

            var redirect = GetRedirect(response);
            if (redirect is null || hop >= _context.MaxRedirects)
                return response;

            var (status, location) = redirect.Value;
            var from = current.RequestUri!.ToString();
            var nextUri = new Uri(current.RequestUri, location);
            var crossHost = !string.Equals(current.RequestUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase);

            _context.RecordRedirect(from, nextUri.ToString(), (int)status, crossHost);
            _context.Trace?.Invoke($"  ↳ {status} {Shorten(from)} → {Shorten(nextUri.ToString())}"
                                   + (crossHost ? " (跨主机，已移除凭据)" : ""));

            var next = Clone(current, nextUri, stripCredentials: crossHost);
            response.Dispose();
            current.Dispose();
            current = next;

            if (_context.Verbose) LogRequest(current);
        }
    }

    void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var (name, value) in _context.HeadersFor(request.RequestUri!))
        {
            // 别重复塞同名头（HttpRequestMessage 有些头只能通过强类型属性设置）
            if (request.Headers.TryGetValues(name, out _)) continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    void CollectCookies(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies) && response.RequestMessage?.RequestUri is { } uri)
            _context.CaptureCookies(uri, cookies);
    }

    void LogRequest(HttpRequestMessage request)
    {
        var trace = _context.Trace;
        if (trace is null) return;

        trace($"→ {request.Method} {request.RequestUri}");
        foreach (var header in request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = string.Join(", ", header.Value);
            trace($"  {header.Key}: {RequestContext.Redact(header.Key, value)}");
        }
    }

    void LogResponse(HttpResponseMessage response)
    {
        var trace = _context.Trace;
        if (trace is null) return;

        var status = (int)response.StatusCode;
        trace($"← {status} {response.ReasonPhrase}");

        foreach (var header in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = string.Join(", ", header.Value);
            trace($"  {header.Key}: {RequestContext.Redact(header.Key, value)}");
        }
        if (response.Content.Headers.ContentLength is { } length)
            trace($"  Content-Length: {length}");
    }

    static (HttpStatusCode Status, Uri Location)? GetRedirect(HttpResponseMessage response)
    {
        if (response.Headers.Location is not { } location) return null;

        return (int)response.StatusCode switch
        {
            301 or 302 or 303 or 307 or 308 => ((HttpStatusCode)response.StatusCode, location),
            _ => null,
        };
    }

    static HttpRequestMessage Clone(HttpRequestMessage source, Uri? uri = null, bool stripCredentials = false)
    {
        var clone = new HttpRequestMessage(source.Method, uri ?? source.RequestUri)
        {
            Version = source.Version,
        };

        foreach (var header in source.Headers)
        {
            if (stripCredentials && RequestContext.IsSensitive(header.Key)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            // 下载器只发 GET/HEAD，没有 body；这里仍做通用处理以防将来扩展。
            clone.Content = source.Content;
            foreach (var header in source.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    static string Shorten(string value) => value.Length <= 80 ? value : value[..77] + "…";
}
