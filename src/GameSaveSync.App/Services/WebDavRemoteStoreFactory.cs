using GameSaveSync.Core.Diagnostics;
using GameSaveSync.WebDav;

namespace GameSaveSync.App.Services;

/// <summary>集中构造 WebDavRemoteStore（注入日志器，便于诊断页查看上传细节）。</summary>
public sealed class WebDavRemoteStoreFactory
{
    private readonly IAppLogger _logger;

    public WebDavRemoteStoreFactory(IAppLogger logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public WebDavRemoteStore Create(WebDavOptions options) => new(options, _logger);
}
