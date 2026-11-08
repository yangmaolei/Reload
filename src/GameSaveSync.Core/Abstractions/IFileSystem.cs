namespace GameSaveSync.Core.Abstractions;

/// <summary>文件元数据快照（stat 结果）。</summary>
public readonly record struct FileEntry(long Size, long MtimeMs, FileAttributes Attributes)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>大小与 mtime 都一致才认为"元数据未变"（仅用于 L2 过滤，不作为内容判断）。</summary>
    public bool MetadataEquals(in FileEntry other) => Size == other.Size && MtimeMs == other.MtimeMs;
}

/// <summary>
/// 文件系统原语。Core 不直接调用 Win32；实现（Infrastructure.PhysicalFileSystem）
/// 负责 \\?\ 长路径前缀、跳过 junction/symlink、保留原始大小写。
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    FileEntry Stat(string path);

    /// <summary>递归枚举文件，返回绝对路径（保留磁盘上的原始大小写），不跟随重解析点。</summary>
    IReadOnlyList<string> EnumerateFiles(string root, bool recursive);

    /// <summary>递归枚举目录，返回绝对路径，不跟随重解析点。</summary>
    IReadOnlyList<string> EnumerateDirectories(string root, bool recursive);

    Stream OpenRead(string path);

    /// <summary>以 Create（覆盖）模式打开写入流；实现需处理长路径与独占。</summary>
    Stream OpenCreate(string path);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    /// <summary>删除目录；recursive=true 时连同子目录一起删。</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>overwrite=true 时走 MoveFileEx(MOVEFILE_REPLACE_EXISTING) 语义的原子替换。</summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void SetAttributes(string path, FileAttributes attributes);

    string GetFullPath(string path);

    string Combine(string left, string right);

    /// <summary>取相对路径（保留原始大小写），left 必须是 root 的绝对路径。</summary>
    string GetRelativePath(string root, string path);
}
