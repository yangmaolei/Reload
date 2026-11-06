namespace GameSaveSync.Core.Covers;

/// <summary>
/// 极简 JPEG 尺寸读取：只解析文件头里的 SOF 段，拿到宽高。
/// <para>
/// 用途：把旧版缓存（不分形态的 <c>steam-&lt;appId&gt;.jpg</c>）归类到
/// 「竖屏长图」或「方形头图」，避免升级后老用户的海报消失。
/// 只读文件前若干字节，不解码像素，代价可忽略。
/// </para>
/// </summary>
public static class JpegSizeReader
{
    /// <summary>需要读取的最大字节数（SOF 段通常在文件头几百字节内）。</summary>
    private const int MaxHeaderBytes = 128 * 1024;

    /// <summary>
    /// 读取 JPEG 的宽高；不是合法 JPEG 或读失败时返回 null。
    /// </summary>
    public static (int Width, int Height)? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[MaxHeaderBytes];
        var read = 0;
        while (read < header.Length)
        {
            var n = stream.Read(header, read, header.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return Parse(header, read);
    }

    /// <summary>从字节缓冲区解析 JPEG 宽高。</summary>
    internal static (int Width, int Height)? Parse(byte[] data, int length)
    {
        if (length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            // 不是 JPEG（PNG 等）
            return null;
        }

        var i = 2;
        while (i + 3 < length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];

            // 填充字节：0xFF 0xFF 继续
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // 没有长度字段的标记
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (i + 3 >= length)
            {
                break;
            }

            var segmentLength = (data[i + 2] << 8) | data[i + 3];
            if (segmentLength < 2)
            {
                break;
            }

            // SOF0..SOF15（跳过 DHT=C4 / JPG=C8 / DAC=CC）
            var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF &&
                                marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isStartOfFrame)
            {
                if (i + 9 >= length)
                {
                    break;
                }

                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }

            i += 2 + segmentLength;
        }

        return null;
    }
}
