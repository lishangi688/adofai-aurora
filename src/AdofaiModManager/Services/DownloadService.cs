using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AdofaiModManager.Services;

public sealed record DownloadProgress(long Received, long? Total)
{
    public int Percent => Total is > 0
        ? (int)Math.Clamp(Received * 100 / Total.Value, 0, 100)
        : -1;
}

/// <summary>
/// 统一下载器：
///  · 支持进度回调；
///  · 服务器支持 Range 且文件较大时，用多段并发下载；
///  · 不支持 Range / 分段失败时自动回退到单流下载。
/// </summary>
public static class DownloadService
{
    private const long MinSegmentSize = 8L * 1024 * 1024;
    private const int MaxSegments = 4;

    public static async Task DownloadAsync(
        HttpClient http,
        string url,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var probe = await TryProbeAsync(http, url, ct);
        if (probe is { Total: > 0 } range &&
            range.Total >= MinSegmentSize &&
            range.AcceptRanges)
        {
            try
            {
                await DownloadSegmentedAsync(http, url, destination, range.Total.Value, progress, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[download] 分段下载失败，回退单线程：{ex.Message}");
                TryDelete(destination);
            }
        }

        await DownloadSingleAsync(http, url, destination, progress, ct);
    }

    private sealed record RangeProbe(long? Total, bool AcceptRanges);

    private static async Task<RangeProbe?> TryProbeAsync(
        HttpClient http,
        string url,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                return new RangeProbe(
                    response.Content.Headers.ContentRange?.Length,
                    response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase));
            }

            return new RangeProbe(
                response.Content.Headers.ContentLength,
                response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadSingleAsync(
        HttpClient http,
        string url,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        progress?.Report(new DownloadProgress(0, total));
        var lastPercent = -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);

        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            received += count;

            if (total is > 0)
            {
                var percent = (int)Math.Clamp(received * 100 / total.Value, 0, 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }
        }

        progress?.Report(new DownloadProgress(received, total ?? received));
    }

    private static async Task DownloadSegmentedAsync(
        HttpClient http,
        string url,
        string destination,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var segmentCount = (int)Math.Min(
            MaxSegments,
            Math.Max(2, total / MinSegmentSize));
        var segmentSize = (total + segmentCount - 1) / segmentCount;
        var parts = new string[segmentCount];
        var directory = Path.Combine(Path.GetTempPath(), $"aurora-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        long received = 0;
        var lastPercent = -1;
        var reportLock = new object();

        void Report(long delta)
        {
            var current = Interlocked.Add(ref received, delta);
            var percent = total > 0 ? (int)Math.Clamp(current * 100 / total, 0, 100) : -1;
            if (percent == lastPercent && current < total)
            {
                return;
            }

            lock (reportLock)
            {
                if (percent == lastPercent && current < total)
                {
                    return;
                }

                lastPercent = percent;
                progress?.Report(new DownloadProgress(current, total));
            }
        }

        try
        {
            progress?.Report(new DownloadProgress(0, total));

            var tasks = new List<Task>(segmentCount);
            for (var i = 0; i < segmentCount; i++)
            {
                var index = i;
                var start = i * segmentSize;
                var end = Math.Min(total - 1, start + segmentSize - 1);
                var part = Path.Combine(directory, $"part-{i:00}");
                parts[i] = part;

                tasks.Add(Task.Run(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct).ConfigureAwait(false);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new InvalidDataException($"服务器未返回分段内容（{response.StatusCode}）。");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = new FileStream(
                        part,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous);

                    var buffer = new byte[81920];
                    long written = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        written += count;
                        Report(count);
                    }

                    if (written != end - start + 1)
                    {
                        throw new InvalidDataException(
                            $"分段 {index} 长度不正确：期望 {end - start + 1}，实际 {written}。");
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            await using (var output = new FileStream(
                             destination,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                foreach (var part in parts)
                {
                    await using var input = new FileStream(
                        part,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.Asynchronous);
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }
            }

            if (new FileInfo(destination).Length != total)
            {
                throw new InvalidDataException("合并后的文件大小与远端不一致。");
            }

            progress?.Report(new DownloadProgress(total, total));
        }
        finally
        {
            foreach (var part in parts)
            {
                TryDelete(part);
            }

            TryDeleteDirectory(directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
