using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AudioEditor.App.Controls;
using AudioEditor.App.ViewModels;

namespace AudioEditor.App;

public partial class MainWindow : Window
{
    private const double AutoScrollMargin = 40;
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>
    /// Below this window width the timeline header has no room for every grid control in the worst
    /// case (a two-digit clip count, the playhead in a tempo section from a three-digit bar).
    /// </summary>
    private const double CompactHeaderWidth = 900;

    /// <summary>
    /// Below this window width the toolbar has no room for every label in the worst case (the beat
    /// grid on, a timeline over ten minutes long, the playhead past bar 100).
    /// </summary>
    private const double CompactToolbarWidth = 1070;

    public static readonly DependencyProperty IsHeaderCompactProperty = DependencyProperty.Register(
        nameof(IsHeaderCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty IsToolbarCompactProperty = DependencyProperty.Register(
        nameof(IsToolbarCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private Point? dragStart;
    private ListBoxItem? dragItem;

    /// <summary>While the timeline is dragged with the middle button: where it started.</summary>
    private (ScrollViewer Scroller, double X, double Offset)? timelinePan;

    public MainWindow()
    {
        InitializeComponent();
        TempoMapPopup.CustomPopupPlacementCallback = PlaceUnderTempoMapButton;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// Whether the window is too narrow for the whole timeline header, so Snap to and the add
    /// tempo change button are shown in the Tempo popup instead.
    /// </summary>
    public bool IsHeaderCompact
    {
        get => (bool)GetValue(IsHeaderCompactProperty);
        private set => SetValue(IsHeaderCompactProperty, value);
    }

    /// <summary>Whether the window is too narrow for the toolbar's labels, so File, Add files and Remove show only their icons.</summary>
    public bool IsToolbarCompact
    {
        get => (bool)GetValue(IsToolbarCompactProperty);
        private set => SetValue(IsToolbarCompactProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        IsHeaderCompact = sizeInfo.NewSize.Width < CompactHeaderWidth;
        IsToolbarCompact = sizeInfo.NewSize.Width < CompactToolbarWidth;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WPF has no event for the horizontal wheel (a thumb wheel, or tilting the wheel).
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_MOUSEHWHEEL)
            return IntPtr.Zero;

        // The high word is the distance, 120 per notch; positive is to the right.
        int delta = (short)((long)wParam >> 16);
        if (EditorWave.IsMouseOver)
            EditorWave.Pan(delta / 120.0);
        else if (TimelineList.IsMouseOver && FindChild<ScrollViewer>(TimelineList) is { } scroller)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + delta);
        else
            return IntPtr.Zero;

        // Some mouse drivers only keep sending the message while it is answered with 1.
        handled = true;
        return 1;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox box)
            TextBoxCommit.Commit(box);
        if (!ViewModel.ConfirmCloseProject())
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e) => ViewModel.Dispose();

    /// <summary>The File button opens its menu below it, the way a menu bar would.</summary>
    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = FileMenuButton.ContextMenu;
        menu.PlacementTarget = FileMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        var modifiers = Keyboard.Modifiers;
        ICommand? command = (e.Key, modifiers) switch
        {
            (Key.N, ModifierKeys.Control) => vm.NewProjectCommand,
            (Key.O, ModifierKeys.Control | ModifierKeys.Shift) => vm.OpenProjectCommand,
            (Key.S, ModifierKeys.Control) => vm.SaveProjectCommand,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => vm.SaveProjectAsCommand,
            (Key.O, ModifierKeys.Control) => vm.AddFilesCommand,
            (Key.E, ModifierKeys.Control) => vm.ExportCommand,
            (Key.Left, ModifierKeys.Control) => vm.MoveLeftCommand,
            (Key.Right, ModifierKeys.Control) => vm.MoveRightCommand,
            (Key.Space, ModifierKeys.None) => vm.PlayPauseCommand,
            (Key.Space, ModifierKeys.Shift) => vm.PreviewCommand,
            (Key.Escape, ModifierKeys.None) => vm.StopCommand,
            (Key.Home, ModifierKeys.None) => vm.GoToStartCommand,
            (Key.Delete, ModifierKeys.None) => vm.RemoveClipCommand,
            (Key.I, ModifierKeys.None) => vm.SetTrimStartCommand,
            (Key.O, ModifierKeys.None) => vm.SetTrimEndCommand,
            (Key.G, ModifierKeys.None) => vm.BeatGrid.ToggleCommand,
            (Key.M, ModifierKeys.None) => vm.BeatGrid.ToggleMetronomeCommand,
            _ => null,
        };
        if (command is null)
            return;

        if (Keyboard.FocusedElement is TextBox box)
        {
            // Plain keys belong to the text box while typing.
            if ((modifiers & ModifierKeys.Control) == 0)
                return;
            // Shortcuts act on what has been typed so far, so Ctrl+S saves it.
            TextBoxCommit.Commit(box);
        }

        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// A click in the window outside the text box being typed in ends the typing, also where the
    /// click lands on something that doesn't take focus itself (the waveform, a label, empty
    /// space). Otherwise the box keeps the keyboard and Space types into it instead of playing.
    /// Clicks in popups (the tempo map, a text box's context menu) are left alone.
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox { IsMouseOver: false } ||
            e.OriginalSource is not Visual source || !IsAncestorOf(source))
            return;

        // The window hands focus back to the element it remembers, so forget the box first.
        FocusManager.SetFocusedElement(this, null);
        Focus();
    }

    /// <summary>Up and Down step the tempo, starting from what was typed.</summary>
    private void OnTempoBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var grid = ViewModel.BeatGrid;
        ICommand? command = e.Key switch
        {
            Key.Up => grid.IncreaseBpmCommand,
            Key.Down => grid.DecreaseBpmCommand,
            _ => null,
        };
        if (command is null)
            return;

        var box = (TextBox)sender;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        command.Execute(null);
        box.SelectAll();
        e.Handled = true;
    }

    // ----- Timeline: click to seek (snapped to the beat grid unless Alt is held), drag to reorder -----

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        dragStart = dragItem is null ? null : e.GetPosition(TimelineList);
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (timelinePan is { } pan)
        {
            pan.Scroller.ScrollToHorizontalOffset(pan.Offset - (e.GetPosition(TimelineList).X - pan.X));
            e.Handled = true;
            return;
        }

        if (dragStart is not { } start || dragItem is not { DataContext: ClipViewModel clip } item || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(TimelineList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        dragStart = null;
        dragItem = null;
        DragDrop.DoDragDrop(item, new DataObject(typeof(ClipViewModel), clip), DragDropEffects.Move);
        HideInsertionMarker();
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is not null && dragItem is { DataContext: ClipViewModel clip } item)
        {
            bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
            ViewModel.SeekTimeline(clip, e.GetPosition(item).X / Math.Max(1, item.ActualWidth), snap);
        }
        dragStart = null;
        dragItem = null;
    }

    /// <summary>
    /// Dragging with the middle button pans the timeline. The scroller takes the mouse rather
    /// than the list box, which lets go of it on a move without the left button down.
    /// </summary>
    private void OnTimelinePanStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || FindChild<ScrollViewer>(TimelineList) is not { } scroller || !scroller.CaptureMouse())
            return;

        timelinePan = (scroller, e.GetPosition(TimelineList).X, scroller.HorizontalOffset);
        scroller.Cursor = Cursors.ScrollWE;
        e.Handled = true;
    }

    private void OnTimelinePanEnd(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || timelinePan is not { } pan)
            return;

        pan.Scroller.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnTimelineLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (timelinePan is not { } pan || pan.Scroller.IsMouseCaptured)
            return;

        pan.Scroller.ClearValue(CursorProperty);
        timelinePan = null;
    }

    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            ViewModel.ZoomTimelineBy(e.Delta / 120.0);
        else if (FindChild<ScrollViewer>(TimelineList) is { } scroller)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel.TimelineViewportWidth = e.NewSize.Width - TimelineList.Padding.Left - TimelineList.Padding.Right;

    private void OnTimelineDragOver(object sender, DragEventArgs e)
    {
        bool isClip = e.Data.GetDataPresent(typeof(ClipViewModel));
        bool isFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Handled = true;
        if (!isClip && !isFiles)
        {
            e.Effects = DragDropEffects.None;
            HideInsertionMarker();
            return;
        }

        e.Effects = isClip ? DragDropEffects.Move : DragDropEffects.Copy;
        var point = e.GetPosition(TimelineList);
        AutoScroll(point.X);
        ShowInsertionMarker(InsertionIndex(point));
    }

    private void OnTimelineDragLeave(object sender, DragEventArgs e) => HideInsertionMarker();

    private void OnTimelineDrop(object sender, DragEventArgs e)
    {
        int index = InsertionIndex(e.GetPosition(TimelineList));
        HideInsertionMarker();
        e.Handled = true;

        if (e.Data.GetData(typeof(ClipViewModel)) is ClipViewModel clip)
            ViewModel.MoveClip(clip, index);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _ = ViewModel.OpenFilesAsync(files, index);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _ = ViewModel.OpenFilesAsync(files);
        e.Handled = true;
    }

    /// <summary>The index a clip dropped at <paramref name="point"/> should get.</summary>
    private int InsertionIndex(Point point)
    {
        int count = TimelineList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (TimelineList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            double left = container.TranslatePoint(new Point(0, 0), TimelineList).X;
            if (point.X < left + container.ActualWidth / 2)
                return i;
        }
        return count;
    }

    private void ShowInsertionMarker(int index)
    {
        int count = TimelineList.Items.Count;
        if (count == 0)
        {
            HideInsertionMarker();
            return;
        }

        bool atEnd = index >= count;
        if (TimelineList.ItemContainerGenerator.ContainerFromIndex(atEnd ? count - 1 : index) is not FrameworkElement container)
            return;

        // Items have a 6 px gap on their right; centre the marker in it.
        double left = container.TranslatePoint(new Point(0, 0), TimelineList).X;
        double x = atEnd ? left + container.ActualWidth + 3 : left - 3;
        Canvas.SetLeft(InsertionMarker, x - InsertionMarker.Width / 2);
        Canvas.SetTop(InsertionMarker, 8);
        InsertionMarker.Height = Math.Max(0, container.ActualHeight - 4);
        InsertionMarker.Visibility = Visibility.Visible;
    }

    private void HideInsertionMarker() => InsertionMarker.Visibility = Visibility.Collapsed;

    private void AutoScroll(double x)
    {
        if (FindChild<ScrollViewer>(TimelineList) is not { } scroller)
            return;
        if (x < AutoScrollMargin)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - 12);
        else if (x > TimelineList.ActualWidth - AutoScrollMargin)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + 12);
    }

    /// <summary>
    /// Opens the tempo map below its button with their right edges lined up, so it stays inside
    /// the window. The popup's margin only makes room for the shadow, so it is left out.
    /// </summary>
    private CustomPopupPlacement[] PlaceUnderTempoMapButton(Size popupSize, Size targetSize, Point offset)
    {
        // The sizes are in device pixels.
        double shadow = ((FrameworkElement)TempoMapPopup.Child).Margin.Right * VisualTreeHelper.GetDpi(TempoMapButton).DpiScaleX;
        var position = new Point(targetSize.Width - popupSize.Width + shadow, targetSize.Height + offset.Y);
        return [new CustomPopupPlacement(position, PopupPrimaryAxis.Horizontal)];
    }

    // ----- Clip editor -----

    private void OnEditorZoomIn(object sender, RoutedEventArgs e) => EditorWave.ZoomIn();

    private void OnEditorZoomOut(object sender, RoutedEventArgs e) => EditorWave.ZoomOut();

    private void OnEditorZoomToTrim(object sender, RoutedEventArgs e) => EditorWave.ZoomToTrim();

    private void OnEditorZoomFit(object sender, RoutedEventArgs e) => EditorWave.ZoomToFit();

    private void OnGainDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedClip is { } clip)
            clip.GainDb = 0;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
