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
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
            };
            using var rangeResponse = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { Range = new RangeHeaderValue(0, 0) } // 请求第一个字节
            };
            using var rangeResponse = httpClient.Send(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            return ParseRangeResponse(rangeResponse);
        }
        catch
        {
            return (false, null);
        }
    }

    private static (bool SupportsRange, long? FileSize) ParseRangeResponse(
        HttpResponseMessage response)
    {
        // 检查状态码
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            var contentLength = response.Content.Headers.ContentLength;
            return (false, contentLength > 0 ? contentLength : null);
        }

        // 优先从Content-Range获取文件大小
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.HasLength == true)
            return (true, contentRange.Length);

        // 最后尝试从当前响应的Content-Length获取
        var currentContentLength = response.Content.Headers.ContentLength;
        return (true, currentContentLength > 0 ? currentContentLength : null);
    }
}
