using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>
/// 图片标注工具条：独立悬浮窗口（微信式，位于贴图正下方、完全在图片外部），
/// 跟随贴图移动。按钮仍由 PinWindow 接管行为。
/// </summary>
public partial class AnnotationToolbarWindow : Window
{
    internal AnnotationToolbarWindow(PinWindow owner)
    {
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) => Win32Helper.HideFromAltTab(this);
    }

    internal Button PenButton => PART_BtnPen;
    internal Button TextButton => PART_BtnText;
    internal Button MosaicButton => PART_BtnMosaic;
    internal Button SizeSmallButton => PART_BtnSizeSmall;
    internal Button SizeMidButton => PART_BtnSizeMid;
    internal Button SizeLargeButton => PART_BtnSizeLarge;
    internal Button UndoButton => PART_BtnUndo;
    internal Button ExitButton => PART_BtnExit;
    internal Button DoneButton => PART_BtnDone;
    internal StackPanel ColorPanel => PART_ColorPanel;

    /// <summary>贴图下方居中摆放；屏幕放不下时放到贴图上方。</summary>
    internal void PlaceBelowOwner(double gapDip)
    {
        if (Owner is not PinWindow owner || ActualWidth == 0)
            return;
        var x = owner.Left + (owner.Width - ActualWidth) / 2;
        var y = owner.Top + owner.Height + gapDip;
        if (y + ActualHeight > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            y = owner.Top - ActualHeight - gapDip;
        if (x < SystemParameters.VirtualScreenLeft)
            x = SystemParameters.VirtualScreenLeft;
        Left = x;
        Top = y;
    }
}
