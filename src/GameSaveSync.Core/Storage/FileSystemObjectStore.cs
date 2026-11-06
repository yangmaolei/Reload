using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;

namespace GameSaveSync.Core.Storage;

/// <summary>
/// 已暂存但未提交的对象。写入流程固定为：写到临时文件 → 算哈希 → 校验稳定 → rename 进 CAS。
/// 进程在任意点被强杀，最多留下 tmp 文件（启动时清理），绝不会留下半截的正式对象。
/// </summary>
public sealed class StagedObject : IDisposable
{
    private bool _disposed;

    internal StagedObject(string hash, string tempPath, long size)
    {
        Hash = hash;
        TempPath = tempPath;
        Size = size;
    }

    public string Hash { get; }

    internal string TempPath { get; }

    public long Size { get; }

    internal bool Committed { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Committed)
        {
            return;
        }

        try
        {
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }
        catch
        {
            // 临时文件清不掉不影响正确性，启动时会统一清理
        }
    }
}

public interface IObjectStore
{
    bool Contains(string hash);

    string GetObjectPath(string hash);

    /// <summary>把源文件读入暂存区并算哈希（不提交）。读取失败（占用/权限）抛 IOException。</summary>
    Task<StagedObject> StageFromFileAsync(string sourcePath, HashIoOptions? ioOptions, CancellationToken ct);

    /// <summary>把流写入暂存区并算哈希（不提交）。</summary>
    Task<StagedObject> StageFromStreamAsync(Stream content, CancellationToken ct);

    /// <summary>用给定哈希校验后写入 CAS（已存在则幂等跳过）。M1 的 IObjectStore.PutAsync。</summary>
    Task PutAsync(string hash, Stream content, CancellationToken ct);

    /// <summary>提交暂存对象：目标已存在则丢弃临时文件（返回 false），否则 rename（返回 true）。</summary>
    bool Commit(StagedObject staged);

    Stream OpenReadAsync(string hash, CancellationToken ct);

    /// <summary>删除对象（仅保留策略会调用；返回是否真的删掉了文件）。</summary>
    bool Delete(string hash);

    /// <summary>启动时清理崩溃残留的 tmp 文件。</summary>
    int CleanupTemporaryFiles();
}

public sealed class FileSystemObjectStore : IObjectStore
{
    private readonly AppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IHashService _hashService;
    private readonly HashSet<string> _ensuredBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bucketGate = new();

    public FileSystemObjectStore(AppPaths paths, IFileSystem fileSystem, IHashService hashService)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _fileSystem.CreateDirectory(_paths.ObjectsDir);
        _fileSystem.CreateDirectory(_paths.ObjectsTempDir);
    }

    /// <summary>
    /// 是否对每个对象调用 FlushFileBuffers（fsync）。
    /// 默认关闭：写入走"临时名 → 校验 → rename"，进程被杀不会留下半截对象；
    /// 极端断电仍可能丢数据。开启后每个对象多约 6ms（5000 个小文件 ≈ 35s 实测）。
    /// </summary>
    public bool DurableWrites { get; set; }

    public string GetObjectPath(string hash) => _paths.ObjectPath(NormalizeHash(hash));

    public bool Contains(string hash)
    {
        var path = GetObjectPath(hash);
        return _fileSystem.FileExists(path);
    }

    public async Task<StagedObject> StageFromFileAsync(
        string sourcePath,
        HashIoOptions? ioOptions,
        CancellationToken ct)
    {
        var options = ioOptions ?? HashIoOptions.Default;
        var tempPath = NewTempPath();

        await using var source = _fileSystem.OpenRead(sourcePath);
        await using var destination = _fileSystem.OpenCreate(tempPath);

        var buffer = new byte[Math.Max(4096, options.ChunkSize)];
        using var hasher = _hashService.CreateHasher();
        long total = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hasher.Update(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;

                if (options.DelayBetweenChunksMs > 0)
                {
                    await Task.Delay(options.DelayBetweenChunksMs, ct).ConfigureAwait(false);
                }
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            if (DurableWrites && destination is FileStream fileStream)
            {
                fileStream.Flush(true);
            }

            var hash = hasher.Finalize();
            return new StagedObject(hash, tempPath, total);
        }
        catch
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task<StagedObject> StageFromStreamAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tempPath = NewTempPath();
        await using var destination = _fileSystem.OpenCreate(tempPath);
        var buffer = new byte[1024 * 1024];
        using var hasher = _hashService.CreateHasher();
        long total = 0;

        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hasher.Update(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            if (DurableWrites && destination is FileStream fileStream)
            {
                fileStream.Flush(true);
            }

            return new StagedObject(hasher.Finalize(), tempPath, total);
        }
        catch
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task PutAsync(string hash, Stream content, CancellationToken ct)
    {
        var normalized = NormalizeHash(hash);
        using var staged = await StageFromStreamAsync(content, ct).ConfigureAwait(false);
        if (!string.Equals(staged.Hash, normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"对象内容哈希与期望不一致：期望 {normalized}，实际 {staged.Hash}");
        }

        if (!Commit(staged))
        {
            return;
        }
    }

    public bool Commit(StagedObject staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var normalized = NormalizeHash(staged.Hash);
        var finalPath = _paths.ObjectPath(normalized);

        EnsureBucket(normalized);

        try
        {
            // 先试 rename（目标已存在会抛 IOException），省掉一次 FileExists 探测
            _fileSystem.MoveFile(staged.TempPath, finalPath, overwrite: false);
            staged.Committed = true;
            return true;
        }
        catch (IOException)
        {
            if (_fileSystem.FileExists(finalPath))
            {
                // 内容寻址：同 hash 一定同内容，重复写入幂等
                TryDelete(staged.TempPath);
                staged.Committed = true;
                return false;
            }

            throw;
        }
    }

    /// <summary>桶目录只创建一次（最多 256 个桶，避免每个对象一次 mkdir 系统调用）。</summary>
    private void EnsureBucket(string hash)
    {
        var bucket = _paths.BucketDirectory(hash);
        lock (_bucketGate)
        {
            if (_ensuredBuckets.Contains(bucket))
            {
                return;
            }
        }

        // 先创建，成功后再标记"已建"：
        // 反过来（先标记后创建）一旦 CreateDirectory 失败（磁盘满/权限/杀软锁），
        // 该桶会被永久标记为已建 → 之后所有同前缀对象都写不进去。
        _fileSystem.CreateDirectory(bucket);

        lock (_bucketGate)
        {
            _ensuredBuckets.Add(bucket);
        }
    }

    public Stream OpenReadAsync(string hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetObjectPath(hash);
        if (!_fileSystem.FileExists(path))
        {
            throw new FileNotFoundException($"对象不存在：{hash}", path);
        }

        return _fileSystem.OpenRead(path);
    }

    public bool Delete(string hash)
    {
        var path = GetObjectPath(hash);
        if (!_fileSystem.FileExists(path))
        {
            return false;
        }

        _fileSystem.DeleteFile(path);
        return true;
    }

    public int CleanupTemporaryFiles()
    {
        if (!_fileSystem.DirectoryExists(_paths.ObjectsTempDir))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in _fileSystem.EnumerateFiles(_paths.ObjectsTempDir, recursive: false))
        {
            TryDelete(file);
            removed++;
        }

        return removed;
    }

    private string NewTempPath() =>
        Path.Combine(_paths.ObjectsTempDir, $"{Guid.NewGuid():N}.part");

    private void TryDelete(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch
        {
            // 忽略：启动清理会兜底
        }
    }

    private static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return hash.Trim().ToLowerInvariant();
    }
}
