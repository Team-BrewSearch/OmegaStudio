using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OmegaStudio.ViewModels;
namespace OmegaStudio.Views;
public sealed class Outliner : ListBox
{
    private const double Indent = 16;
    public static readonly StyledProperty<IReadOnlyList<TreeItemViewModel>?> RootsProperty =
        AvaloniaProperty.Register<Outliner, IReadOnlyList<TreeItemViewModel>?>(nameof(Roots));
    public static readonly StyledProperty<TreeItemViewModel?> SelectedRowProperty =
        AvaloniaProperty.Register<Outliner, TreeItemViewModel?>(
            nameof(SelectedRow), defaultBindingMode: BindingMode.TwoWay);
    private readonly OutlinerRows _rows = [];
    public Outliner()
    {
        ItemsSource = _rows;
        AutoScrollToSelectedItem = false;
        _rows.Refreshed += ShowSelection;
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }
    public IReadOnlyList<TreeItemViewModel>? Roots
    {
        get => GetValue(RootsProperty);
        set => SetValue(RootsProperty, value);
    }
    public TreeItemViewModel? SelectedRow
    {
        get => GetValue(SelectedRowProperty);
        set => SetValue(SelectedRowProperty, value);
    }
    protected override Type StyleKeyOverride => typeof(ListBox);
    public static TreeItemViewModel? RowUnder(object? source)
    {
        return (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TreeItemViewModel;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RootsProperty)
        {
            _rows.Show(Roots);
        }
        else if (change.Property == SelectedRowProperty)
        {
            if (SelectedRow is { } row)
            {
                _ = _rows.Reveal(row);
            }
            ShowSelection();
        }
        else if (change.Property == SelectedItemProperty && SelectedItem is TreeItemViewModel picked)
        {
            SelectedRow = picked;
        }
    }
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is ListBoxItem box && item is TreeItemViewModel row)
        {
            box.Padding = new Thickness(Indent * row.Depth, 0, 0, 0);
        }
    }
    private void ShowSelection()
    {
        int index = SelectedRow is { } row ? _rows.IndexOf(row) : -1;
        if (index != SelectedIndex)
        {
            SelectedIndex = index;
            if (index >= 0)
            {
                ScrollIntoView(index);
            }
        }
    }
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        if (SelectedItem is not TreeItemViewModel row)
        {
            e.Handled = e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown && MoveTo(0);
            return;
        }
        int index = SelectedIndex;
        e.Handled = e.Key switch
        {
            Key.Up => MoveTo(index - 1),
            Key.Down => MoveTo(index + 1),
            Key.Home => MoveTo(0),
            Key.End => MoveTo(ItemCount - 1),
            Key.PageUp => MoveTo(index - Page()),
            Key.PageDown => MoveTo(index + Page()),
            Key.Right when row is { HasChildren: true, IsExpanded: false } => Toggle(row),
            Key.Right when row.HasChildren => MoveTo(index + 1),
            Key.Left when row is { HasChildren: true, IsExpanded: true } => Toggle(row),
            Key.Left when row.Parent is { } parent && _rows.IndexOf(parent) is >= 0 and int up => MoveTo(up),
            Key.Enter or Key.Space when row.HasChildren => Toggle(row),
            _ => false,
        };
    }
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is null
            && RowUnder(e.Source) is { HasChildren: true } row)
        {
            e.Handled = Toggle(row);
        }
    }
    private static bool Toggle(TreeItemViewModel row)
    {
        row.IsExpanded = !row.IsExpanded;
        return true;
    }
    private bool MoveTo(int index)
    {
        if (ItemCount == 0)
        {
            return false;
        }
        index = Math.Clamp(index, 0, ItemCount - 1);
        SelectedIndex = index;
        ScrollIntoView(index);
        _ = ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
        return true;
    }
    private int Page()
    {
        double height = ContainerFromIndex(SelectedIndex)?.Bounds.Height ?? 0;
        return height > 0 && Scroll is { } scroll ? Math.Max(1, (int)(scroll.Viewport.Height / height) - 1) : 1;
    }
}
