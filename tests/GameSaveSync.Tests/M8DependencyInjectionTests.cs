using GameSaveSync.Core.Online;
using Xunit;

namespace GameSaveSync.Tests;

/// <summary>
/// 防回归：DI 构造函数歧义。
/// </summary>
public sealed class M8DependencyInjectionTests
{
    /// <summary>
    /// <see cref="SavePathRefreshService"/> 只能有一个构造函数。
    /// <para>
    /// 曾经同时提供「单个数据源」和「数据源集合」两个构造函数，DI 容器判定为歧义，
    /// 启动时抛 InvalidOperationException —— 而异常被 MessageBox 吞掉，
    /// 表现是"应用启动了但界面永远不出来"，极难定位。
    /// </para>
    /// </summary>
    [Fact]
    public void SavePathRefreshService_HasExactlyOneConstructor()
    {
        var constructors = typeof(SavePathRefreshService).GetConstructors();

        Assert.Single(constructors);
    }

    /// <summary>存档路径数据源实现里不能有多个构造函数，否则同样会让 DI 歧义。</summary>
    [Theory]
    [InlineData(typeof(LudusaviManifestProvider))]
    [InlineData(typeof(PcGamingWikiProvider))]
    public void OnlineProviders_HaveExactlyOneConstructor(Type providerType)
    {
        Assert.Single(providerType.GetConstructors());
    }
}
