using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameSaveSync.App.ViewModels;

namespace GameSaveSync.App.Views.Controls;

/// <summary>
/// 海报切换控件：显示一张海报，带左右切换按钮与分页指示器。
/// <para>
/// 支持两种形态（竖屏长图 / 方形头图），切换时做淡入淡出过渡。
/// 图片用 <c>Stretch="Uniform"</c> 等比缩放，绝不变形；容器高度按形态宽高比计算，
/// 窄屏下宽度受限则高度同步收缩，不会溢出。
/// </para>
/// </summary>
public partial class PosterSwitcher : UserControl
{
    /// <summary>淡入淡出时长。</summary>
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(220));

    private Storyboard? _fadeStoryboard;
    private GameListItemViewModel? _boundItem;

    public PosterSwitcher()
    {
        InitializeComponent();

        _fadeStoryboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1d,
            To = 0d,
            Duration = FadeDuration,
            AutoReverse = true,
        };
        Storyboard.SetTarget(fade, PosterImage);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _fadeStoryboard.Children.Add(fade);

        Loaded += (_, _) => Rebind();
        DataContextChanged += (_, _) => Rebind();
    }

    /// <summary>当前绑定的条目（供测试与外部查询）。</summary>
    internal GameListItemViewModel? BoundItem => _boundItem;

    private void Rebind()
    {
        if (_boundItem is not null)
        {
            _boundItem.PropertyChanged -= ItemOnPropertyChanged;
            _boundItem = null;
        }

        if (DataContext is not GameListItemViewModel item)
        {
            IndicatorRow.ItemsSource = null;
            return;
        }

        _boundItem = item;
        item.PropertyChanged += ItemOnPropertyChanged;
        UpdateIndicators();
    }

    private void ItemOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameListItemViewModel.SelectedPosterKind))
        {
            StartFade();
            UpdateIndicators();
        }
        else if (e.PropertyName is nameof(GameListItemViewModel.AvailablePosters))
        {
            UpdateIndicators();
        }
    }

    /// <summary>按可用海报数生成指示点。</summary>
    private void UpdateIndicators()
    {
        if (_boundItem is null)
        {
            IndicatorRow.ItemsSource = null;
            return;
        }

        IndicatorRow.ItemsSource = _boundItem.AvailablePosters.Keys.ToList();
    }

    /// <summary>切换海报时的淡入淡出过渡。</summary>
    private void StartFade()
    {
        // AutoReverse 的淡出→淡入：切换瞬间先淡出一半再淡回来，视觉上是"闪一下"
        _fadeStoryboard?.Stop();
        _fadeStoryboard?.Begin();
    }

    /// <summary>圆角裁剪（Image 不自带裁剪，需显式给 Clip）。</summary>
    internal static Geometry BuildClip(double width, double height, double radius) =>
        new RectangleGeometry(new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)), radius, radius);
}
