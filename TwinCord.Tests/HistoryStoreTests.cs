using System.IO;
using System.Text.Json;
using TwinCord;
using TwinCord.Services;

namespace TwinCord.Tests;

public class HistoryStoreTests
{
    [Fact]
    public void Load_SkipsNullElementsAndNormalizesMissingText()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, AppConstants.HistoryFileName);
        File.WriteAllText(path, """
            [
              null,
              { "Time": "2026-10-19T01:02:03", "Account": 1, "AccountName": null, "Text": null },
              { "Time": "2026-10-19T02:03:04", "Account": 2, "AccountName": "  ", "Text": "hello" }
            ]
            """);

        var store = new HistoryStore(temp.Path);
        store.Load();

        Assert.Equal(2, store.Items.Count);
        Assert.Equal("Acc1", store.Items[0].AccountName);
        Assert.Equal("", store.Items[0].Text);
        Assert.Equal("Acc2", store.Items[1].AccountName);
        Assert.Equal("hello", store.Items[1].Text);
    }

    [Fact]
    public void Load_KeepsOnlyNewestThirty()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, AppConstants.HistoryFileName);
        var records = Enumerable.Range(0, 35)
            .Select(i => new NotificationRecord(
                new DateTime(2026, 10, 19, 0, 0, 0).AddMinutes(i), 1, "A", i.ToString()))
            .ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(records));

        var store = new HistoryStore(temp.Path);
        store.Load();

        Assert.Equal(30, store.Items.Count);
        Assert.Equal("0", store.Items[0].Text);
        Assert.Equal("29", store.Items[^1].Text);
    }

    [Fact]
    public void Load_ClearsExistingItemsOnSuccessfulReload()
    {
        using var temp = new TempFolder();
        var store = new HistoryStore(temp.Path);
        store.Add(new NotificationRecord(DateTime.Now, 1, "A", "old"));

        File.WriteAllText(Path.Combine(temp.Path, AppConstants.HistoryFileName), "[]");
        store.Load();

        Assert.Empty(store.Items);
    }

    [Fact]
    public void Load_ToleratesBrokenJson()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, AppConstants.HistoryFileName), "{broken");

        var store = new HistoryStore(temp.Path);
        store.Load();

        Assert.Empty(store.Items);
    }

    [Fact]
    public void Add_KeepsNewestThirtyAndPersists()
    {
        using var temp = new TempFolder();
        var store = new HistoryStore(temp.Path);
        for (int i = 0; i < 35; i++)
            store.Add(new NotificationRecord(DateTime.Now.AddSeconds(i), 1, "A", i.ToString()));

        Assert.Equal(30, store.Items.Count);
        Assert.Equal("34", store.Items[0].Text);

        var reloaded = new HistoryStore(temp.Path);
        reloaded.Load();
        Assert.Equal(30, reloaded.Items.Count);
        Assert.Equal("34", reloaded.Items[0].Text);
    }
}
