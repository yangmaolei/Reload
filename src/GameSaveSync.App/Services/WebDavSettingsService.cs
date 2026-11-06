using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Security;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;
using GameSaveSync.WebDav;

namespace GameSaveSync.App.Services;

/// <summary>
/// 可热替换的远端存储：设置页保存新 WebDAV 配置后，不必重建整个上传服务。
/// </summary>
public sealed class ConfigurableRemoteStore : IRemoteStore, IDisposable
{
    private readonly object _gate = new();
    private IRemoteStore _inner = NullRemoteStore.Instance;
    private IDisposable? _disposableInner;

    public bool IsConfigured => _inner.IsConfigured;

    public void SetInner(IRemoteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        IDisposable? previous;
        lock (_gate)
        {
            previous = _disposableInner;
            _inner = store;
            _disposableInner = store as IDisposable;
        }

        previous?.Dispose();
    }

    public Task PutObjectAsync(string hash, Stream content, CancellationToken ct) =>
        _inner.PutObjectAsync(hash, content, ct);

    public Task PutManifestAsync(Guid gameId, SnapshotManifest manifest, CancellationToken ct) =>
        _inner.PutManifestAsync(gameId, manifest, ct);

    public Task<Stream?> GetObjectAsync(string hash, CancellationToken ct) =>
        _inner.GetObjectAsync(hash, ct);

    public Task<string?> GetManifestJsonAsync(Guid gameId, string fileName, CancellationToken ct) =>
        _inner.GetManifestJsonAsync(gameId, fileName, ct);

    public Task DeleteObjectAsync(string hash, CancellationToken ct) => _inner.DeleteObjectAsync(hash, ct);

    public Task DeleteManifestAsync(Guid gameId, string fileName, CancellationToken ct) =>
        _inner.DeleteManifestAsync(gameId, fileName, ct);

    public Task TestConnectionAsync(CancellationToken ct) => _inner.TestConnectionAsync(ct);

    public Task EnsureLayoutAsync(string deviceId, CancellationToken ct) => _inner.EnsureLayoutAsync(deviceId, ct);

    public Task<RemoteIndexFetch> GetIndexAsync(string? ifNoneMatch, CancellationToken ct) =>
        _inner.GetIndexAsync(ifNoneMatch, ct);

    public Task<RemoteIndexWrite> PutIndexAsync(string json, string? ifMatch, string? ifNoneMatch, CancellationToken ct) =>
        _inner.PutIndexAsync(json, ifMatch, ifNoneMatch, ct);

    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct) =>
        _inner.ListAsync(relativeDirectory, ct);

    public Task PutDeviceAsync(GameSaveSync.Core.Remote.DeviceDocument device, CancellationToken ct) =>
        _inner.PutDeviceAsync(device, ct);

    public Task<IReadOnlyList<GameSaveSync.Core.Remote.DeviceDocument>> ListDevicesAsync(CancellationToken ct) =>
        _inner.ListDevicesAsync(ct);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposableInner?.Dispose();
            _disposableInner = null;
            _inner = NullRemoteStore.Instance;
        }
    }
}

/// <summary>
/// WebDAV 配置的读写：密码经 DPAPI 加密后存 SQLite，任何日志里都不出现明文。
/// </summary>
public sealed class WebDavSettingsService
{
    private readonly ISettingsRepository _settings;
    private readonly ICredentialProtector _protector;
    private readonly ConfigurableRemoteStore _remote;
    private readonly WebDavRemoteStoreFactory _factory;

    public WebDavSettingsService(
        ISettingsRepository settings,
        ICredentialProtector protector,
        ConfigurableRemoteStore remote,
        WebDavRemoteStoreFactory factory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<WebDavOptions> LoadAsync(CancellationToken ct)
    {
        var options = new WebDavOptions
        {
            BaseUrl = await _settings.GetAsync(SettingsRepository.WebDavUrlKey, ct).ConfigureAwait(false) ?? string.Empty,
            RemoteRoot = await _settings.GetAsync(SettingsRepository.WebDavRootKey, ct).ConfigureAwait(false)
                         ?? AppPaths.DefaultRemoteRoot,
            Username = await _settings.GetAsync(SettingsRepository.WebDavUserKey, ct).ConfigureAwait(false),
            TrustedCertificateThumbprint = await _settings
                .GetAsync(SettingsRepository.WebDavCertThumbKey, ct).ConfigureAwait(false),
        };

        var protectedPassword = await _settings.GetAsync(SettingsRepository.WebDavPasswordKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(protectedPassword) && _protector.IsAvailable)
        {
            try
            {
                options.Password = _protector.Unprotect(protectedPassword);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // 换机器/换用户后 DPAPI 解不开：当作未配置，提示用户重新输入
                options.Password = null;
            }
        }

        return options;
    }

    public async Task SaveAsync(WebDavOptions options, string? plainPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _settings.SetAsync(SettingsRepository.WebDavUrlKey, options.BaseUrl?.Trim(), ct).ConfigureAwait(false);
        await _settings.SetAsync(SettingsRepository.WebDavRootKey, options.RemoteRoot?.Trim(), ct).ConfigureAwait(false);
        await _settings.SetAsync(SettingsRepository.WebDavUserKey, options.Username, ct).ConfigureAwait(false);
        await _settings
            .SetAsync(SettingsRepository.WebDavCertThumbKey, options.TrustedCertificateThumbprint, ct)
            .ConfigureAwait(false);

        if (plainPassword is not null)
        {
            var cipher = _protector.IsAvailable ? _protector.Protect(plainPassword) : null;
            await _settings.SetAsync(SettingsRepository.WebDavPasswordKey, cipher, ct).ConfigureAwait(false);
        }

        await ApplyAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>把当前配置装配成可用的远端存储。</summary>
    public Task ApplyAsync(WebDavOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            _remote.SetInner(NullRemoteStore.Instance);
            return Task.CompletedTask;
        }

        _remote.SetInner(_factory.Create(options));
        return Task.CompletedTask;
    }

    public Task<ConnectionTestResult> TestAsync(
        WebDavOptions options,
        string? password,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidate = options.Clone();
        if (password is not null)
        {
            candidate.Password = password;
        }

        using var store = _factory.Create(candidate);
        return store.TestConnectionDetailedAsync(ct);
    }
}
