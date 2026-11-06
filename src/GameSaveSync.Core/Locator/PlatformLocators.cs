using System.Text.Json;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Locator;

/// <summary>Epic：<c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item</c>（JSON）。</summary>
public sealed class EpicLocator
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;

    public EpicLocator(IFileSystem fileSystem, ILocatorEnvironment environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<DiscoveredGame> Scan(out IReadOnlyList<string> warnings)
    {
        var games = new List<DiscoveredGame>();
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestDir in _environment.GetEpicManifestDirs())
        {
            foreach (var path in LocatorIo.FilesWithExtensions(_fileSystem, manifestDir, false, ".item"))
            {
                var text = LocatorIo.TryReadText(_fileSystem, path);
                if (text is null)
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(text, JsonOptions);
                    var root = document.RootElement;

                    var name = GetString(root, "DisplayName") ?? GetString(root, "AppName");
                    var installDir = GetString(root, "InstallLocation");
                    var catalogId = GetString(root, "CatalogItemId") ?? GetString(root, "AppName");

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
                    {
                        continue;
                    }

                    if (!_fileSystem.DirectoryExists(installDir))
                    {
                        continue;
                    }

                    if (!seen.Add(installDir))
                    {
                        continue;
                    }

                    games.Add(new DiscoveredGame
                    {
                        Name = name,
                        Source = GameSource.Epic,
                        ExternalId = catalogId,
                        InstallDir = installDir,
                        ExeNames = LocatorIo.FindExeNamesDeep(_fileSystem, installDir),
                        Engine = EngineDetector.Detect(_fileSystem, installDir),
                    });
                }
                catch (JsonException ex)
                {
                    issues.Add($"解析 Epic 清单失败：{Path.GetFileName(path)}（{ex.Message}）");
                }
            }
        }

        warnings = issues;
        return games;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}

/// <summary>GOG：扫 <c>GOG Games\*</c>，用 <c>goggame-*.info</c> 拿名字与 gameId。</summary>
public sealed class GogLocator
{
    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;

    public GogLocator(IFileSystem fileSystem, ILocatorEnvironment environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<DiscoveredGame> Scan(out IReadOnlyList<string> warnings)
    {
        var games = new List<DiscoveredGame>();
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDir in _environment.GetGogGameDirs())
        {
            foreach (var gameDir in SafeDirs(baseDir))
            {
                if (!seen.Add(gameDir))
                {
                    continue;
                }

                var info = LocatorIo.FilesWithExtensions(_fileSystem, gameDir, false, ".info")
                    .FirstOrDefault(f => Path.GetFileName(f).StartsWith("goggame-", StringComparison.OrdinalIgnoreCase));

                var name = Path.GetFileName(gameDir);
                string? gameId = null;

                if (info is not null)
                {
                    var text = LocatorIo.TryReadText(_fileSystem, info);
                    if (text is not null)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = JsonCommentHandling.Skip,
                            });

                            foreach (var property in document.RootElement.EnumerateObject())
                            {
                                if (property.NameEquals("name") && property.Value.ValueKind == JsonValueKind.String)
                                {
                                    name = property.Value.GetString() ?? name;
                                }
                                else if (property.NameEquals("gameId") && property.Value.ValueKind == JsonValueKind.String)
                                {
                                    gameId = property.Value.GetString();
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            issues.Add($"解析 GOG 信息失败：{Path.GetFileName(info)}（{ex.Message}）");
                        }
                    }
                }

                games.Add(new DiscoveredGame
                {
                    Name = name,
                    Source = GameSource.Gog,
                    ExternalId = gameId,
                    InstallDir = gameDir,
                    ExeNames = LocatorIo.FindExeNamesDeep(_fileSystem, gameDir),
                    Engine = EngineDetector.Detect(_fileSystem, gameDir),
                });
            }
        }

        warnings = issues;
        return games;
    }

    private IReadOnlyList<string> SafeDirs(string baseDir)
    {
        try
        {
            return _fileSystem.DirectoryExists(baseDir)
                ? _fileSystem.EnumerateDirectories(baseDir, recursive: false)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Ubisoft：<c>%LOCALAPPDATA%\Ubisoft Game Launcher\savegames\&lt;userId&gt;\&lt;gameId&gt;</c>。
/// 游戏名无法从目录反推（只有数字 ID），因此以"Ubisoft 存档 &lt;id&gt;"命名，交给用户在界面上改。
/// </summary>
public sealed class UbisoftLocator
{
    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;

    public UbisoftLocator(IFileSystem fileSystem, ILocatorEnvironment environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<DiscoveredGame> Scan(out IReadOnlyList<string> warnings)
    {
        var games = new List<DiscoveredGame>();
        var issues = new List<string>();

        foreach (var root in _environment.GetUbisoftSaveRoots())
        {
            foreach (var userDir in SafeDirs(root))
            {
                foreach (var gameDir in SafeDirs(userDir))
                {
                    if (!LocatorIo.DirectoryHasAnyFile(_fileSystem, gameDir))
                    {
                        continue;
                    }

                    var gameId = Path.GetFileName(gameDir);
                    var userId = Path.GetFileName(userDir);

                    games.Add(new DiscoveredGame
                    {
                        Name = $"Ubisoft 存档 {gameId}",
                        Source = GameSource.Ubisoft,
                        ExternalId = $"{userId}/{gameId}",
                        InstallDir = null,
                        Engine = GameEngine.Unknown,
                        SuggestedRoots = { gameDir },
                    });
                }
            }
        }

        warnings = issues;
        return games;
    }

    private IReadOnlyList<string> SafeDirs(string directory)
    {
        try
        {
            return _fileSystem.DirectoryExists(directory)
                ? _fileSystem.EnumerateDirectories(directory, recursive: false)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
