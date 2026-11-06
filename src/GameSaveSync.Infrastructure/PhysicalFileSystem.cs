using GameSaveSync.Core.Abstractions;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// 真实文件系统实现。三条硬约束：
/// 1) 全部走 <c>\\?\</c> 长路径；
/// 2) 不进入 junction / symlink（AttributesToSkip = ReparsePoint），防目录环；
/// 3) 保留磁盘上的原始大小写，比较交给上层用 lower-case 归一化。
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public static PhysicalFileSystem Instance { get; } = new();

    /// <summary>游戏运行时读取要用 FileShare.ReadWrite|Delete，否则会被独占句柄挡住。</summary>
    public FileShare ReadShare { get; init; } = FileShare.ReadWrite | FileShare.Delete;

    public bool FileExists(string path) => File.Exists(LongPath.Ensure(path));

    public bool DirectoryExists(string path) => Directory.Exists(LongPath.Ensure(path));

    public FileEntry Stat(string path)
    {
        var full = LongPath.Ensure(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            if (Directory.Exists(full))
            {
                var attributes = File.GetAttributes(full);
                return new FileEntry(0, 0, attributes | FileAttributes.Directory);
            }

            throw new FileNotFoundException($"文件不存在：{path}", path);
        }

        return new FileEntry(info.Length, info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond, info.Attributes);
    }

    public IReadOnlyList<string> EnumerateFiles(string root, bool recursive)
    {
        var directory = new DirectoryInfo(LongPath.Ensure(root));
        if (!directory.Exists)
        {
            return Array.Empty<string>();
        }

        return directory
            .EnumerateFiles("*", BuildOptions(recursive))
            .Select(f => LongPath.Strip(f.FullName))
            .ToList();
    }

    public IReadOnlyList<string> EnumerateDirectories(string root, bool recursive)
    {
        var directory = new DirectoryInfo(LongPath.Ensure(root));
        if (!directory.Exists)
        {
            return Array.Empty<string>();
        }

        return directory
            .EnumerateDirectories("*", BuildOptions(recursive))
            .Select(d => LongPath.Strip(d.FullName))
            .ToList();
    }

    public Stream OpenRead(string path) =>
        new FileStream(
            LongPath.Ensure(path),
            FileMode.Open,
            FileAccess.Read,
            ReadShare,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

    public Stream OpenCreate(string path) =>
        new FileStream(
            LongPath.Ensure(path),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

    public void CreateDirectory(string path) => Directory.CreateDirectory(LongPath.Ensure(path));

    public void DeleteFile(string path) => File.Delete(LongPath.Ensure(path));

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        // overwrite=true → MoveFileEx(MOVEFILE_REPLACE_EXISTING)，目标已存在时原子替换
        File.Move(LongPath.Ensure(sourcePath), LongPath.Ensure(destinationPath), overwrite);
    }

    public void SetAttributes(string path, FileAttributes attributes) =>
        File.SetAttributes(LongPath.Ensure(path), attributes);

    public string GetFullPath(string path) => Path.GetFullPath(LongPath.Strip(path));

    public string Combine(string left, string right) => Path.Combine(left, right);

    public string GetRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(LongPath.Strip(root), LongPath.Strip(path));
        return relative.Replace('/', '\\');
    }

    private static EnumerationOptions BuildOptions(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        // 不跟随 junction / symlink，避免目录环
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
    };
}
