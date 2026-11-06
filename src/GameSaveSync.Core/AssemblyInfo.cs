using System.Runtime.CompilerServices;

// 测试工程需要访问内部工具类（如 JPEG 头解析）。
[assembly: InternalsVisibleTo("GameSaveSync.Tests")]
