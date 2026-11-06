using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MioKit.LockBreaker.Views;

public partial class ProcessListUserControl : UserControl
{
    private Window? _ownerWindow;

    public ProcessListUserControl()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_ownerWindow is not null)
        {
            _ownerWindow.SizeChanged += OnOwnerSizeChanged;
            ApplyResponsiveLayout(_ownerWindow.Bounds.Width);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_ownerWindow is not null)
            _ownerWindow.SizeChanged -= OnOwnerSizeChanged;
        _ownerWindow = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnOwnerSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double windowWidth)
    {
        var narrow = windowWidth < 820;
        ContentLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,286");
        ContentLayout.RowDefinitions = new RowDefinitions(narrow ? "*,118" : "*");
        ContentLayout.RowSpacing = narrow ? 8 : 0;
        DetailsPane.Padding = new Thickness(narrow ? 10 : 12);
        Grid.SetColumn(DetailsPane, narrow ? 0 : 1);
        Grid.SetRow(DetailsPane, narrow ? 1 : 0);
    }
}
