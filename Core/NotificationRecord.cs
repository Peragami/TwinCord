namespace TwinCord;

/// <summary>通知履歴の1件。View層(HistoryWindow)ではなくCoreに置き、保存側から参照できるようにする。</summary>
public record NotificationRecord(DateTime Time, int Account, string AccountName, string Text);
