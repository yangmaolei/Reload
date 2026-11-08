using System.Globalization;
using GameSaveSync.App.Converters;
using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Tests;

/// <summary>
/// 多选功能：勾选状态、事件通知、IntToBool 转换器。
/// <para>
/// 关键约束：多选（勾选）与单选（详情页）是两套独立状态 ——
/// 勾选不能影响"点卡片进详情"，进详情也不能清掉勾选。
/// </para>
/// </summary>
public sealed class M7SelectionTests
{
    private static GameListItemViewModel NewItem(string name)
    {
        return new GameListItemViewModel(new Game { Name = name });
    }

    // ============================================================
    // 一、勾选状态
    // ============================================================

    [Fact]
    public void IsChecked_DefaultsToFalse()
    {
        var item = NewItem("游戏A");

        Assert.False(item.IsChecked);
    }

    [Fact]
    public void SettingIsChecked_RaisesCheckedChanged()
    {
        var item = NewItem("游戏A");
        var raised = 0;
        item.CheckedChanged += _ => raised++;

        item.IsChecked = true;
        item.IsChecked = false;

        Assert.Equal(2, raised);
    }

    [Fact]
    public void SettingIsCheckedToSameValue_DoesNotRaiseEvent()
    {
        var item = NewItem("游戏A");
        var raised = 0;
        item.CheckedChanged += _ => raised++;

        item.IsChecked = true;
        item.IsChecked = true;

        // CommunityToolkit 只在值真正变化时触发
        Assert.Equal(1, raised);
    }

    [Fact]
    public void IsChecked_IsIndependentOfSingleSelection()
    {
        var item = NewItem("游戏A");

        // 勾选是纯粹的布尔状态，不牵动单选
        item.IsChecked = true;
        Assert.True(item.IsChecked);
        Assert.Equal("游戏A", item.Name);
    }

    // ============================================================
    // 二、IntToBool 转换器（RadioButton 绑定自启动方式）
    // ============================================================

    [Fact]
    public void IntToBool_MatchesOnlyTheTargetValue()
    {
        var converter = new IntToBoolConverter();

        Assert.True((bool)converter.Convert(1, typeof(bool), 1, CultureInfo.InvariantCulture)!);
        Assert.False((bool)converter.Convert(2, typeof(bool), 1, CultureInfo.InvariantCulture)!);
        Assert.True((bool)converter.Convert(0, typeof(bool), 0, CultureInfo.InvariantCulture)!);
        Assert.False((bool)converter.Convert(1, typeof(bool), 0, CultureInfo.InvariantCulture)!);
    }

    [Fact]
    public void IntToBool_AcceptsStringParameter()
    {
        var converter = new IntToBoolConverter();

        // XAML 里写 ConverterParameter="1" 有可能被解析成字符串
        Assert.True((bool)converter.Convert(1, typeof(bool), "1", CultureInfo.InvariantCulture)!);
        Assert.False((bool)converter.Convert(2, typeof(bool), "1", CultureInfo.InvariantCulture)!);
    }

    [Fact]
    public void IntToBool_ConvertBack_ReturnsTargetIndex()
    {
        var converter = new IntToBoolConverter();

        Assert.Equal(2, converter.ConvertBack(true, typeof(int), 2, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IntToBool_ConvertBack_WhenUnchecked_DoesNotOverwrite()
    {
        var converter = new IntToBoolConverter();

        // 取消勾选时不能把索引改成 0 —— 那会误触发"关闭自启动"
        var result = converter.ConvertBack(false, typeof(int), 2, CultureInfo.InvariantCulture);

        Assert.Same(System.Windows.Data.Binding.DoNothing, result);
    }

    [Fact]
    public void IntToBool_ConvertBack_UnknownParameter_FallsBackToZero()
    {
        var converter = new IntToBoolConverter();

        Assert.Equal(0, converter.ConvertBack(true, typeof(int), "abc", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IntToBool_Convert_NullValue_IsFalse()
    {
        var converter = new IntToBoolConverter();

        Assert.False((bool)converter.Convert(null, typeof(bool), 0, CultureInfo.InvariantCulture)!);
    }
}
