using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Pinshot.Views;

/// <summary>
/// 数字微调框：可直接输入，也可用右侧上下按钮调节（步进 1），自动夹紧范围。
/// </summary>
public sealed class NumericSpinner : Border
{
    private readonly TextBox _text = new()
    {
        Width = 56,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly Button _up = CreateButton("⌃");
    private readonly Button _down = CreateButton("⌄");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericSpinner),
        new PropertyMetadata(1, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumericSpinner), new PropertyMetadata(1));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumericSpinner), new PropertyMetadata(50));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public NumericSpinner()
    {
        _up.Click += (_, _) => Value = Math.Min(Maximum, Value + 1);
        _down.Click += (_, _) => Value = Math.Max(Minimum, Value - 1);
        _text.LostKeyboardFocus += (_, _) => Commit();
        _text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };

        var buttons = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        buttons.Children.Add(_up);
        buttons.Children.Add(_down);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_text);
        panel.Children.Add(buttons);
        Child = panel;
    }

    private static Button CreateButton(string glyph) => new()
    {
        Content = glyph,
        Width = 20,
        Height = 13,
        FontSize = 9,
        Padding = new Thickness(0),
        Margin = new Thickness(0, 0, 0, 1),
        BorderThickness = new Thickness(1),
    };

    private void Commit()
    {
        Value = int.TryParse(_text.Text.Trim(), out var parsed)
            ? Math.Clamp(parsed, Minimum, Maximum)
            : Value;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericSpinner spinner)
            spinner._text.Text = spinner.Value.ToString();
    }
}
