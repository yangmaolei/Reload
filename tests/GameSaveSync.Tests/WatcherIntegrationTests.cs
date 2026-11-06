using System.Diagnostics;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// 真实环境集成测试：不打桩，直接验证 Windows 上的进程轮询与目录监视。
/// 这两个是 M2 自动化的地基，必须确认在真机上真的会触发。
/// </summary>
public sealed class WatcherIntegrationTests
{
    [Fact]
    public async Task ProcessWatcher_DetectsRealStartAndExitByPolling()
    {
        var logger = new InMemoryAppLogger();
        using var watcher = new WindowsProcessWatcher(logger, pollInterval: TimeSpan.FromMilliseconds(250));

        var game = new Game
        {
            Name = "cmd-probe",
            InstallDir = Environment.GetFolderPath(Environment.SpecialFolder.System),
            Enabled = true,
        };
        game.ExeNames.Add("cmd.exe");

        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.GameStarted += (_, pid) => started.TrySetResult(pid);
        watcher.GameExited += (_, pid) => exited.TrySetResult(pid);
        watcher.Start(new[] { game });

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);

        var startedOk = await WaitAsync(started.Task, TimeSpan.FromSeconds(8));
        var exitedOk = await WaitAsync(exited.Task, TimeSpan.FromSeconds(15));

        Assert.True(startedOk, "轮询必须在几秒内发现 cmd.exe 启动");
        Assert.True(exitedOk, "轮询必须发现 cmd.exe 退出");
    }

    [Fact]
    public async Task SaveFolderWatcher_RaisesChangeAndOverflowFlag()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("Saved Games/Watch", ("a.sav", TestEnvironment.Bytes(64)));
        var game = await env.AddGameAsync("Watch", default, dir);

        using var watcher = new SaveFolderWatcher(env.FileSystem, env.Tokens, env.Logger);
        var changes = new List<(Guid GameId, bool Overflow)>();
        var signal = new SemaphoreSlim(0);
        watcher.Changed += (gameId, overflow) =>
        {
            lock (changes)
            {
                changes.Add((gameId, overflow));
            }

            signal.Release();
        };

        watcher.Start(new[] { game });

        // 给 FileSystemWatcher 一点初始化时间
        await Task.Delay(300);
        File.WriteAllBytes(Path.Combine(dir, "b.sav"), TestEnvironment.Bytes(128));

        var raised = await signal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(raised, "写文件后必须收到变更事件");

        lock (changes)
        {
            Assert.Contains(changes, c => c.GameId == game.Id && !c.Overflow);
        }
    }

    private static async Task<bool> WaitAsync(Task<int> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        return ReferenceEquals(completed, task);
    }
}
