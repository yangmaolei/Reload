namespace GameSaveSync.Core.Abstractions;

/// <summary>递增哈希器：一边写盘一边算哈希，避免二次读文件。</summary>
public interface IHasher : IDisposable
{
    void Update(ReadOnlySpan<byte> data);

    /// <summary>返回 64 位小写十六进制摘要。</summary>
    string Finalize();
}

/// <summary>内容哈希（BLAKE3，64 位小写十六进制）。绝不使用 ETag / mtime 作为内容标识。</summary>
public interface IHashService
{
    string AlgorithmName { get; }

    IHasher CreateHasher();

    string HashBytes(ReadOnlySpan<byte> data);
}

/// <summary>哈希 IO 节流参数（M2 的 idle 快照用；M1 手动路径不节流）。</summary>
public sealed class HashIoOptions
{
    public static HashIoOptions Default { get; } = new();

    /// <summary>单次读块大小，默认 1 MB（规格要求 ≤ 1 MB）。</summary>
    public int ChunkSize { get; init; } = 1024 * 1024;

    /// <summary>块间延迟毫秒，默认 0。</summary>
    public int DelayBetweenChunksMs { get; init; }
}
