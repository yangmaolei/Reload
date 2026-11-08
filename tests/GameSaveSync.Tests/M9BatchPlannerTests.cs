using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Export;
using GameSaveSync.Core.Models;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// 批量操作的业务决策层：空选择、失效游戏检查、目录备份与 zip 导出。
/// </summary>
public sealed class M7BatchPlannerTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _env.DisposeAsync().AsTask();

    private BatchOperationPlanner NewPlanner()
    {
        var exporter = new BatchSaveExporter(_env.Games, _env.FileSystem, _env.Tokens, _env.Logger);
        return new BatchOperationPlanner(_env.Games, exporter, _env.Logger);
    }

    [Fact]
    public async Task BackupToDirectory_EmptySelection_ReturnsEmptyResult()
    {
        var result = await NewPlanner().BackupToDirectoryAsync(
            Array.Empty<Guid>(),
            Path.Combine(_env.Root, "out"),
            overwriteExisting: true,
            default);

        Assert.Empty(result.Items);
        Assert.Contains("没有", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToZip_EmptySelection_ReturnsEmptyResult()
    {
        var result = await NewPlanner().ExportToZipAsync(
            Array.Empty<Guid>(),
            Path.Combine(_env.Root, "out.zip"),
            default);

        Assert.Empty(result.Items);
        Assert.Contains("没有", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupToDirectory_SelectedGame_WritesFiles()
    {
        var dir = _env.CreateSaveDir("Saved Games/PlanA", ("a.sav", TestEnvironment.Bytes(40, 1)));
        var game = await _env.AddGameAsync("PlanA", default, dir);

        var target = Path.Combine(_env.Root, "plan-out");
        var result = await NewPlanner().BackupToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: true, default);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.TotalFileCount);

        var exported = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
        Assert.Single(exported);
    }

    [Fact]
    public async Task ExportToZip_SelectedGame_WritesArchive()
    {
        var dir = _env.CreateSaveDir("Saved Games/PlanB", ("b.sav", TestEnvironment.Bytes(40, 2)));
        var game = await _env.AddGameAsync("PlanB", default, dir);

        var zip = Path.Combine(_env.Root, "plan.zip");
        var result = await NewPlanner().ExportToZipAsync(new[] { game.Id }, zip, default);

        Assert.True(result.AllSucceeded);
        Assert.True(File.Exists(zip));
        Assert.Equal(1, result.TotalFileCount);
    }

    [Fact]
    public async Task FindMissingGames_ReportsDeletedGameIds()
    {
        var dir = _env.CreateSaveDir("Saved Games/PlanC", ("c.sav", TestEnvironment.Bytes(10, 3)));
        var alive = await _env.AddGameAsync("Alive", default, dir);
        var dead = Guid.NewGuid();

        var missing = await NewPlanner().FindMissingGamesAsync(
            new[] { alive.Id, dead }, default);

        Assert.Single(missing);
        Assert.Equal(dead.ToString("N")[..8], missing[0]);
    }

    [Fact]
    public async Task FindMissingGames_AllAlive_ReturnsEmpty()
    {
        var dir = _env.CreateSaveDir("Saved Games/PlanD", ("d.sav", TestEnvironment.Bytes(10, 4)));
        var game = await _env.AddGameAsync("Alive2", default, dir);

        var missing = await NewPlanner().FindMissingGamesAsync(new[] { game.Id }, default);

        Assert.Empty(missing);
    }

    [Fact]
    public void NothingSelectedMessage_IsUserFriendly()
    {
        Assert.Contains("勾选", BatchOperationPlanner.NothingSelectedMessage, StringComparison.Ordinal);
    }
}
