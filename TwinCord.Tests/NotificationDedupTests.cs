using TwinCord.Services;

namespace TwinCord.Tests;

public class NotificationDedupTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameOriginRepeats_AreNotDuplicates()
    {
        // 本文が同じでも、同じ経路から続けて来たものは別の通知として表示する
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Page, T0));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Page, T0.AddMilliseconds(100)));
    }

    [Fact]
    public void OtherOriginWithinWindow_IsDuplicate()
    {
        // JSフックとネイティブ通知が同じ通知を二重に出す場合だけ抑制する
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Page, T0));
        Assert.True(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Native, T0.AddMilliseconds(500)));
    }

    [Fact]
    public void OtherOriginAfterWindow_IsNotDuplicate()
    {
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Page, T0));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Native, T0.AddSeconds(3)));
    }

    [Fact]
    public void DifferentAccountTitleOrBody_IsNotDuplicate()
    {
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        Assert.False(d.IsDuplicate(1, "A", "hello", NotificationDedup.Origin.Page, T0));
        Assert.False(d.IsDuplicate(2, "A", "hello", NotificationDedup.Origin.Native, T0));
        Assert.False(d.IsDuplicate(1, "B", "hello", NotificationDedup.Origin.Native, T0));
        Assert.False(d.IsDuplicate(1, "A", "world", NotificationDedup.Origin.Native, T0));
    }

    [Fact]
    public void ManyDistinctNotifications_DoNotGrowUnbounded()
    {
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 500; i++)
            d.IsDuplicate(1, "A", "m" + i, NotificationDedup.Origin.Page, T0.AddMilliseconds(i));
        Assert.True(d.Count <= 64, $"件数が増え続けている: {d.Count}");
    }

    [Fact]
    public void StaleEntries_AreRemovedOnPrune()
    {
        var d = new NotificationDedup(TimeSpan.FromSeconds(2));
        // 古い通知を多数積んだあと、時間を進めて大量の新着を入れる(古い分は掃除される)
        for (int i = 0; i < 40; i++)
            d.IsDuplicate(1, "A", "old" + i, NotificationDedup.Origin.Page, T0);
        for (int i = 0; i < 80; i++)
            d.IsDuplicate(1, "A", "new" + i, NotificationDedup.Origin.Page, T0.AddSeconds(30).AddMilliseconds(i));
        Assert.True(d.Count <= 64);
    }
}
