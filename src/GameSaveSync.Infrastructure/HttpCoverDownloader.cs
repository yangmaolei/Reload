using GameSaveSync.Core.Covers;

namespace GameSaveSync.Infrastructure;

/// <summary>封面下载：只接受图片、限时 8 秒、限 4 MB；失败一律当"没有封面"。</summary>
public sealed class HttpCoverDownloader : ICoverDownloader, IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private readonly HttpClient _client;

    public HttpCoverDownloader(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        if (!_client.DefaultRequestHeaders.UserAgent.TryParseAdd("Reload/1.0"))
        {
            // 已经设置过就不管
        }
    }

    public async Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            using var response = await _client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
