using GameSaveSync.Core.Paths;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

public sealed class PathTokenTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public void Expand_ResolvesAllSupportedTokens()
    {
        var path = _env.Tokens.Expand(@"%SAVEDGAMES%\Demo\saves");
        Assert.Equal(Path.Combine(_env.SaveGamesDir, "Demo", "saves"), path);

        var documents = _env.Tokens.Expand(@"%DOCUMENTS%\My Games\X\Saves");
        Assert.StartsWith(Path.Combine(_env.ProfileDir, "Documents"), documents, StringComparison.OrdinalIgnoreCase);

        var appData = _env.Tokens.Expand(@"%APPDATA%\Studio\Game");
        Assert.Contains(@"AppData\Roaming\Studio\Game", appData, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tokenize_RoundTripsBackToToken()
    {
        var absolute = Path.Combine(_env.SaveGamesDir, "中文 游戏", "saves");
        var tokenized = _env.Tokens.Tokenize(absolute);

        Assert.NotNull(tokenized);
        Assert.StartsWith("%SAVEDGAMES%", tokenized);
        Assert.Equal(absolute, _env.Tokens.Expand(tokenized!));
    }

    [Fact]
    public void Tokenize_UsesLongestPrefixMatch()
    {
        var local = Path.Combine(_env.ProfileDir, "AppData", "Local", "Unity", "Save");
        var tokenized = _env.Tokens.Tokenize(local);

        // %LOCALAPPDATA% 比 %APPDATA%/ %USERPROFILE% 更长，必须优先匹配
        Assert.StartsWith("%LOCALAPPDATA%", tokenized);
    }

    [Fact]
    public void Tokenize_ReturnsNullForUnknownRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "saves");
        Assert.Null(_env.Tokens.Tokenize(outside));
    }

    [Fact]
    public void Expand_ThrowsForUnknownToken()
    {
        var ex = Assert.Throws<PathTokenException>(() => _env.Tokens.Expand(@"%NOT_A_TOKEN%\x"));
        Assert.Equal("%NOT_A_TOKEN%", ex.Token);
    }

    [Fact]
    public void TryExpand_ReportsMissingTokenWithoutThrowing()
    {
        var ok = _env.Tokens.TryExpand(@"%NOPE%\saves", out _, out var missing);
        Assert.False(ok);
        Assert.Equal("%NOPE%", missing);
    }

    [Fact]
    public async Task SaveRootResolver_MarksGameNeedsRelocateWhenTokenMissing()
    {
        var game = await _env.AddGameAsync("Missing", saveDirs: Array.Empty<string>());
        game.SaveRoots.Clear();
        game.SaveRoots.Add(new GameSaveSync.Core.Models.SaveRoot { Token = @"%NOPE%\saves", IsTokenized = true });
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        var resolution = SaveRootResolver.Resolve(game, _env.Tokens, _env.FileSystem);

        Assert.False(resolution.IsComplete);
        Assert.Contains("%NOPE%", resolution.MissingTokens);
    }

    [Fact]
    public void GlobMatcher_HandlesCommonPatterns()
    {
        var matcher = new GlobMatcher(new[] { "*.sav", "profiles/*" }, new[] { "*.log" });

        Assert.True(matcher.IsMatch("slot1.sav"));
        Assert.True(matcher.IsMatch(@"deep\nested\slot2.sav"));
        Assert.True(matcher.IsMatch(@"profiles\player1.sav"));
        Assert.False(matcher.IsMatch(@"profiles\nested\data.bin"));   // 单星号不跨目录
        Assert.False(matcher.IsMatch("debug.log"));
        Assert.False(matcher.IsMatch("readme.txt"));

        var deep = new GlobMatcher(new[] { "profiles/**" }, Array.Empty<string>());
        Assert.True(deep.IsMatch(@"profiles\player1\data.bin"));

        var excludeWins = new GlobMatcher(new[] { "*" }, new[] { "cache/*" });
        Assert.True(excludeWins.IsMatch("save.dat"));
        Assert.False(excludeWins.IsMatch(@"cache\temp.bin"));
        Assert.False(excludeWins.IsMatch(@"nested\cache\temp.bin"));
    }

    [Fact]
    public void GlobMatcher_DoubleStarCrossesDirectories()
    {
        var matcher = new GlobMatcher(new[] { "**/*.sav" }, Array.Empty<string>());

        Assert.True(matcher.IsMatch("a.sav"));
        Assert.True(matcher.IsMatch(@"x\y\z\a.sav"));
        Assert.False(matcher.IsMatch("a.txt"));
    }
}
