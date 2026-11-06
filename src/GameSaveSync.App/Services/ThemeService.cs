using System.Windows;
using System.Windows.Media;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Theming;

namespace GameSaveSync.App.Services;

/// <summary>
/// 主题服务：把 MD3 色彩角色注入 Application.Resources。
/// 颜色字典是<strong>同一个实例</strong>，Apply 时 Clear + 重填，所有 DynamicResource 引用即时更新。
/// </summary>
public sealed class ThemeService
{
    public const string SeedSettingKey = "ui.theme.seed";
    public const string ModeSettingKey = "ui.theme.mode";

    private readonly ISettingsRepository? _settings;
    private readonly ResourceDictionary _colors = new();
    private bool _installed;

    public ThemeService(ISettingsRepository? settings = null) => _settings = settings;

    public ThemeSeed Seed { get; private set; } = ThemeSeed.Teal;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>当前实际生效的是亮色还是暗色（跟随系统时解析系统设置）。</summary>
    public bool IsDark => ResolveDark(Mode);

    /// <summary>注册颜色字典（App 启动时调用一次，之后 Apply 即时生效）。</summary>
    public void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (!_installed)
        {
            application.Resources.MergedDictionaries.Add(_colors);
            _installed = true;
        }

        // 同步加载：设置页读 Seed 属性时必须已经是最终值（DB 是本地 SQLite，开销可忽略）
        LoadSaved();
    }

    /// <summary>应用指定主题（重填颜色字典；调用方负责保存偏好）。</summary>
    public void Apply(ThemeSeed seed, ThemeMode mode)
    {
        Seed = seed;
        Mode = mode;
        Fill(ResolveDark(mode));
    }

    /// <summary>读持久化偏好并应用（DB 不可用时静默用默认值）。</summary>
    private void LoadSaved()
    {
        var seed = Seed;
        var mode = Mode;

        if (_settings is not null)
        {
            try
            {
                var seedText = _settings.GetAsync(SeedSettingKey, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                if (Enum.TryParse(seedText, ignoreCase: true, out ThemeSeed parsedSeed) &&
                    Enum.IsDefined(parsedSeed))
                {
                    seed = parsedSeed;
                }

                var modeText = _settings.GetAsync(ModeSettingKey, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                if (Enum.TryParse(modeText, ignoreCase: true, out ThemeMode parsedMode) &&
                    Enum.IsDefined(parsedMode))
                {
                    mode = parsedMode;
                }
            }
            catch
            {
                // 设置读不到就用默认主题
            }
        }

        Apply(seed, mode);
    }

    /// <summary>保存偏好并应用。</summary>
    public async Task SetAsync(ThemeSeed seed, ThemeMode mode)
    {
        Apply(seed, mode);

        if (_settings is null)
        {
            return;
        }

        try
        {
            await _settings.SetAsync(SeedSettingKey, seed.ToString(), CancellationToken.None).ConfigureAwait(true);
            await _settings.SetAsync(ModeSettingKey, mode.ToString(), CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // 保存失败不阻塞 UI（例如 DB 只读）
        }
    }

    private static bool ResolveDark(ThemeMode mode)
    {
        if (mode == ThemeMode.Light)
        {
            return false;
        }

        if (mode == ThemeMode.Dark)
        {
            return true;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void Fill(bool dark)
    {
        var palette = ThemePalettes.For(Seed, dark);
        _colors.BeginInit();
        _colors.Clear();

        foreach (var (key, pc) in palette)
        {
            var color = Color.FromArgb(pc.A, pc.R, pc.G, pc.B);
            _colors[$"Md3.{key}Color"] = color;
            _colors[$"Md3.{key}"] = new SolidColorBrush(color);
        }

        _colors.EndInit();
    }
}
