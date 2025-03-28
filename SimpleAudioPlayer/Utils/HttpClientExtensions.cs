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

            // Range请求验证
            var rangeResponse = await httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
                });

            return ParseRangeResponse(rangeResponse);
        }
        catch(Exception e)
        {
            throw new Exception("Failed to check range support.", e);
        }
    }
    
    public static (bool SupportsRange, long? FileSize) CheckRangeSupportWithSize(
        this HttpClient httpClient,
        string url)
    {
        try
        {
            // Range请求验证
            var rangeResponse = httpClient.Send(
                new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
                });

            return ParseRangeResponse(rangeResponse);
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
        HttpResponseMessage response)
    {
        // 检查状态码
        if (response.StatusCode != HttpStatusCode.PartialContent)
            return (false, null);

        // 优先从Content-Range获取文件大小
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.HasLength == true)
            return (true, contentRange.Length);

        // 最后尝试从当前响应的Content-Length获取
        var currentContentLength = response.Content.Headers.ContentLength;
        return (true, currentContentLength > 0 ? currentContentLength : null);
    }
}