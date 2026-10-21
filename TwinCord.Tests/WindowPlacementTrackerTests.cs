using System.Windows;
using TwinCord.Services;

namespace TwinCord.Tests;

public class WindowPlacementTrackerTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void ClampToVisible_KeepsNormalPosition()
    {
        var (left, top) = WindowPlacementTracker.ClampToVisible(Screen, 100, 80, 1200);

        Assert.Equal(100, left);
        Assert.Equal(80, top);
    }

    [Fact]
    public void ClampToVisible_PullsWindowFromOutside()
    {
        var (left, top) = WindowPlacementTracker.ClampToVisible(Screen, 3000, 2000, 1200);

        Assert.Equal(1840, left); // 右端から80px見える位置
        Assert.Equal(1000, top);  // タイトルバーが画面内に残る位置
    }

    [Fact]
    public void ClampToVisible_KeepsMostlyOffscreenWindowReachable()
    {
        var (left, top) = WindowPlacementTracker.ClampToVisible(Screen, -1300, 100, 1200);

        Assert.Equal(-1120, left); // 右端が80pxだけ画面内
        Assert.Equal(100, top);
    }

    [Fact]
    public void ClampToVisible_KeepsSecondaryMonitorPosition()
    {
        // 左側に置いたセカンドモニター(負の座標)も仮想スクリーン内なら維持する
        var virtualScreen = new Rect(-1920, 0, 3840, 1080);
        var (left, top) = WindowPlacementTracker.ClampToVisible(virtualScreen, -1800, 100, 1200);

        Assert.Equal(-1800, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void ClampToVisible_HandlesEmptyScreen()
    {
        var (left, top) = WindowPlacementTracker.ClampToVisible(new Rect(0, 0, 0, 0), 123, 456, 1200);

        Assert.Equal(123, left);
        Assert.Equal(456, top);
    }
}
