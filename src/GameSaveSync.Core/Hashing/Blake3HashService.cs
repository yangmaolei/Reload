using Blake3;
using GameSaveSync.Core.Abstractions;

namespace GameSaveSync.Core.Hashing;

/// <summary>
/// BLAKE3 内容哈希。存档动辄几百 MB，BLAKE3 比 SHA-256 快 5~10 倍。
/// 输出统一为 64 位小写十六进制（CAS 文件名即此值）。
/// </summary>
public sealed class Blake3HashService : IHashService
{
    public static Blake3HashService Instance { get; } = new();

    public string AlgorithmName => "blake3";

    public IHasher CreateHasher() => new Blake3IncrementalHasher();

    public string HashBytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(Hasher.Hash(data).AsSpan()).ToLowerInvariant();

    private sealed class Blake3IncrementalHasher : IHasher
    {
        private readonly Hasher _hasher = Hasher.New();
        private bool _finalized;

        public void Update(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_finalized, this);
            _hasher.Update(data);
        }

        public string Finalize()
        {
            ObjectDisposedException.ThrowIf(_finalized, this);
            _finalized = true;
            var hash = _hasher.Finalize();
            return Convert.ToHexString(hash.AsSpan()).ToLowerInvariant();
        }

        public void Dispose()
        {
            if (!_finalized)
            {
                _finalized = true;
                try
                {
                    _hasher.Dispose();
                }
                catch
                {
                    // 释放失败无需影响快照结果
                }
            }
        }
    }
}
