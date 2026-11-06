using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.Tests.TestSupport;

/// <summary>可手动拨动的时钟：用来制造"跨天/跨周"的快照，验证 GFS 保留策略。</summary>
public sealed class TestClock : IClock
{
    private long _nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long UtcNowMs => Interlocked.Read(ref _nowMs);

    public DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(UtcNowMs).ToLocalTime();

    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _nowMs, value.ToUnixTimeMilliseconds());

    public void Advance(TimeSpan delta) =>
        Interlocked.Add(ref _nowMs, (long)delta.TotalMilliseconds);
}
