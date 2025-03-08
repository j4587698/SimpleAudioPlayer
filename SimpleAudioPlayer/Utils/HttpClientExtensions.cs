using System.Net;
using System.Net.Http.Headers;

namespace SimpleAudioPlayer.Utils;

public static class HttpClientExtensions
{

    public static async Task<(bool SupportsRange, long? FileSize)> CheckRangeSupportWithSizeAsync(
        this HttpClient httpClient,
        string url)
    {
        try
        {
            // 第一阶段：HEAD请求快速检测
            var headResponse = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            if (!headResponse.IsSuccessStatusCode) return (false, null);

            // 解析关键头部
            var (hasRangeSupport, contentLength) = ParseHeaders(headResponse);

            // 头部信息明确且有效时直接返回
            if (hasRangeSupport && contentLength > 0)
                return (true, contentLength);

            // 第二阶段：Range请求验证
            var rangeResponse = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
                });

            return ParseRangeResponse(rangeResponse, contentLength);
        }
        catch
        {
            return (false, null);
        }
    }
    
    public static (bool SupportsRange, long? FileSize) CheckRangeSupportWithSize(
        this HttpClient httpClient,
        string url)
    {
        try
        {
            // 第一阶段：HEAD请求快速检测
            var headResponse = httpClient.Send(new HttpRequestMessage(HttpMethod.Head, url));
            if (!headResponse.IsSuccessStatusCode) return (false, null);

            // 解析关键头部
            var (hasRangeSupport, contentLength) = ParseHeaders(headResponse);

            // 头部信息明确且有效时直接返回
            if (hasRangeSupport && contentLength > 0)
                return (true, contentLength);

            // 第二阶段：Range请求验证
            var rangeResponse = httpClient.Send(
                new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
                });

            return ParseRangeResponse(rangeResponse, contentLength);
        }
        catch
        {
            return (false, null);
        }
    }

    private static (bool HasRange, long? Length) ParseHeaders(HttpResponseMessage response)
    {
        // 检查Accept-Ranges标头
        var acceptRanges = response.Headers
            .FirstOrDefault(h => h.Key.Equals("Accept-Ranges", StringComparison.OrdinalIgnoreCase))
            .Value?.FirstOrDefault();

        var hasRange = acceptRanges?.Equals("bytes", StringComparison.OrdinalIgnoreCase) ?? false;

        // 获取Content-Length
        var contentLength = response.Content.Headers.ContentLength;

        return (hasRange, contentLength);
    }

    private static (bool SupportsRange, long? FileSize) ParseRangeResponse(
        HttpResponseMessage response,
        long? headContentLength)
    {
        // 检查状态码
        if (response.StatusCode != HttpStatusCode.PartialContent)
            return (false, null);

        // 优先从Content-Range获取文件大小
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.HasLength == true)
            return (true, contentRange.Length);

        // 次选从HEAD请求的Content-Length获取
        if (headContentLength > 0)
            return (true, headContentLength);

        // 最后尝试从当前响应的Content-Length获取
        var currentContentLength = response.Content.Headers.ContentLength;
        return (true, currentContentLength > 0 ? currentContentLength : null);
    }
}