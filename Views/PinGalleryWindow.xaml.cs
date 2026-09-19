using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>
/// 贴图列表窗口（Setuna“参考图名单 / 回收站”）：
/// 参考图名单列出当前所有贴图，双击激活；回收站列出已关闭贴图，双击还原。
/// </summary>
public partial class PinGalleryWindow : Window
{
    public enum GalleryMode
    {
        Active,
        Dustbox,
    }

    private sealed class GalleryItem
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public BitmapSource? Thumbnail { get; init; }
        public required Action Activate { get; init; }
        public Action? Delete { get; init; }
    }

    private readonly GalleryMode _mode;
    private List<GalleryItem> _items = [];

    private PinGalleryWindow(GalleryMode mode)
    {
        _mode = mode;
        InitializeComponent();
        Title = mode == GalleryMode.Active ? "参考图名单" : "回收站";
        PART_ClearButton.Visibility = mode == GalleryMode.Active ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    internal static void ShowActive() => Show(GalleryMode.Active);

    internal static void ShowDustbox() => Show(GalleryMode.Dustbox);

    private static void Show(GalleryMode mode)
    {
        var existing = Application.Current.Windows.OfType<PinGalleryWindow>()
            .FirstOrDefault(w => w._mode == mode);
        if (existing != null)
        {
            existing.Refresh();
            existing.Activate();
            return;
        }
        new PinGalleryWindow(mode).Show();
    }

    private void Refresh()
    {
        _items = [];
        if (_mode == GalleryMode.Active)
        {
            var index = 1;
            foreach (var pin in App.Pins.GetActiveSnapshot())
            {
                var captured = pin;
                var fileName = pin.SourcePath != null
                    ? System.IO.Path.GetFileName(pin.SourcePath)
                    : "";
                _items.Add(new GalleryItem
                {
                    Title = $"#{index++}{(string.IsNullOrEmpty(fileName) ? "" : $"  {fileName}")}",
                    Subtitle = $"{pin.ImageSize.Width}×{pin.ImageSize.Height}",
                    Thumbnail = pin.DisplaySource,
                    Activate = () => captured.BringToFront(),
                });
            }
        }
        else
        {
            var index = 0;
            foreach (var recycled in App.Pins.Dustbox)
            {
                var captured = index;
                BitmapSource? thumb = null;
                try
                {
                    thumb = ImageLoader.ThumbnailOf(recycled.ImageData);
                }
                catch
                {
                    // 缩略图失败不影响列表
                }
                _items.Add(new GalleryItem
                {
                    Title = $"{recycled.ClosedAt:HH:mm:ss} 关闭的贴图",
                    Subtitle = recycled.IsGif ? "GIF 动画" : "图片",
                    Thumbnail = thumb,
                    Activate = () => App.Pins.RestoreFromDustbox(captured),
                    Delete = () => App.Pins.RemoveFromDustbox(captured),
                });
                index++;
            }
        }

        PART_List.Items.Clear();
        foreach (var item in _items)
            PART_List.Items.Add(BuildRow(item));
        if (PART_List.Items.Count > 0)
            PART_List.SelectedIndex = 0;
    }

    private ListBoxItem BuildRow(GalleryItem item)
    {
        // 卡片式条目：图片在上，还原/删除按钮在图片正下方
        var card = new StackPanel { Margin = new Thickness(6) };

        var image = new Image
        {
            Source = item.Thumbnail,
            MaxHeight = 150,
            MaxWidth = 380,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        card.Children.Add(image);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
        };
        if (item.Delete != null)
        {
            var deleteAction = item.Delete;
            var deleteButton = new Button
            {
                Width = 30,
                Height = 26,
                ToolTip = "删除",
                Cursor = Cursors.Hand,
                Content = new System.Windows.Shapes.Path
                {
                    Width = 13,
                    Height = 13,
                    Data = System.Windows.Media.Geometry.Parse("M 2,2 L 12,12 M 12,2 L 2,12"),
                    Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D)),
                    StrokeThickness = 2,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                },
            };
            deleteButton.Click += (_, _) =>
            {
                deleteAction();
                Refresh();
            };
            buttonRow.Children.Add(deleteButton);
        }
        var activateButton = new Button
        {
            Width = 30,
            Height = 26,
            ToolTip = _mode == GalleryMode.Active ? "激活" : "还原",
            Cursor = Cursors.Hand,
            Content = new System.Windows.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = System.Windows.Media.Geometry.Parse("M 2,8 L 6,12 L 13,3"),
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0xA7, 0x5A)),
                StrokeThickness = 2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                Stretch = System.Windows.Media.Stretch.Uniform,
            },
        };
        activateButton.Click += (_, _) =>
        {
            item.Activate();
            if (_mode == GalleryMode.Dustbox)
                Refresh();
        };
        buttonRow.Children.Add(activateButton);
        card.Children.Add(buttonRow);

        var text = new TextBlock
        {
            Text = $"{item.Title}  {item.Subtitle}",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        card.Children.Add(text);

        var row = new ListBoxItem { Content = card, Tag = item };
        return row;
    }

    private void RunSelected()
    {
        if (PART_List.SelectedItem is ListBoxItem { Tag: GalleryItem item })
            item.Activate();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_mode == GalleryMode.Dustbox)
        {
            RunSelected();
            Refresh();
        }
        else
        {
            RunSelected();
        }
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (_mode == GalleryMode.Dustbox)
        {
            RunSelected();
            Refresh();
        }
        else
        {
            RunSelected();
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        App.Pins.ClearDustbox();
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
