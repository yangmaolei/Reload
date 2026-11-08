using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// .NET Framework 4.8 没有这个类型，但 C# 的 record / init-only 成员需要它。
/// 照 .NET 6+ 的定义补一个 polyfill。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}

/// <summary>
/// C# 11 的 required 成员需要这个 attribute。
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
internal sealed class RequiredMemberAttribute : Attribute
{
}

/// <summary>
/// 编译器为 record 生成的辅助类型标记。
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
internal sealed class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string featureName)
    {
        FeatureName = featureName;
    }

    public string FeatureName { get; }
}
