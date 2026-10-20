using System.Windows;

namespace TwinCord;

/// <summary>設定画面のトグル1行(見出し・補足・スイッチ)。</summary>
public partial class ToggleRow : System.Windows.Controls.UserControl
{
    public ToggleRow() => InitializeComponent();

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ToggleRow),
            new PropertyMetadata(""));

    /// <summary>行の見出し。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(ToggleRow),
            new PropertyMetadata(""));

    /// <summary>見出しの下に出す補足。</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(ToggleRow),
            new PropertyMetadata(false));

    /// <summary>スイッチの状態。</summary>
    public bool? IsChecked
    {
        get => (bool?)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}
