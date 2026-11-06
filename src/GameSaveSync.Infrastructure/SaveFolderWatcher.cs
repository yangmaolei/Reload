using System.Runtime.Versioning;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Scheduling;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// 存档目录监视：每个存档根一个 <see cref="FileSystemWatcher"/>，缓冲 64 KB。
/// 捕获 <see cref="InternalBufferOverflowException"/> 后只上报"overflow"，由上层退化为全量扫描 —— 
/// 绝不用事件本身直接触发快照。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SaveFolderWatcher : IFileChangeWatcher
{
    /// <summary>规格要求：64 KB。</summary>
    public const int BufferSize = 64 * 1024;

    private readonly IFileSystem _fileSystem;
    private readonly IPathTokenResolver _tokens;
    private readonly IAppLogger _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private bool _disposed;

    public SaveFolderWatcher(IFileSystem fileSystem, IPathTokenResolver tokens, IAppLogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<Guid, bool>? Changed;

    public void Start(IReadOnlyList<Game> games) => UpdateGames(games);

    public void UpdateGames(IReadOnlyList<Game> games)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // 关闭路径忽略
                }
            }

            _watchers.Clear();

            foreach (var game in games.Where(g => g.Enabled))
            {
                var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
                foreach (var root in resolution.Roots)
                {
                    if (root.AbsolutePath is null || !root.DirectoryExists)
                    {
                        continue;
                    }

                    TryAttach(game, root.AbsolutePath, root.Root.Recursive);
                }
            }
        }

        _logger.Debug($"目录监视已刷新：{_watchers.Count} 个存档根");
    }

    private void TryAttach(Game game, string path, bool recursive)
    {
        try
        {
            var watcher = new FileSystemWatcher(LongPath.Ensure(path))
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = BufferSize,
                EnableRaisingEvents = false,
            };

            var gameId = game.Id;

            void OnChange(object? sender, FileSystemEventArgs e) => Raise(gameId, overflow: false);
            void OnRename(object? sender, RenamedEventArgs e) => Raise(gameId, overflow: false);

            watcher.Changed += OnChange;
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnRename;
            watcher.Error += (_, args) =>
            {
                if (args.GetException() is InternalBufferOverflowException)
                {
                    _logger.Warn($"监视缓冲区溢出（{path}），本次退化为全量扫描");
                    Raise(gameId, overflow: true);
                }
                else
                {
                    _logger.Warn($"目录监视错误（{path}）：{args.GetException()?.Message}");
                }
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            _logger.Warn($"无法监视目录：{path}", ex);
        }
    }

    private void Raise(Guid gameId, bool overflow) => Changed?.Invoke(gameId, overflow);

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }

            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
