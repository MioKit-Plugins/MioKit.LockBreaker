using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MioKit.LockBreaker.Views;
using System.Text.Json;
using Ti.Avalonia.Shadcn;

namespace MioKit.LockBreaker.Preview;

/// <summary>Exports only this preview's own visual tree. No desktop, HWND, or external-window capture is used.</summary>
internal static class PreviewRender
{
    public static bool HasOutput(string[] args) => ReadOption(args, "--render") is not null;

    public static Window CreateWindow(string[] args, string scenario, Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var output = Path.GetFullPath(ReadOption(args, "--render")!);
        var width = ReadSize(args, "--width", 880, 640);
        var height = ReadSize(args, "--height", 620, 420);
        var model = PreviewScenario.CreateModel(scenario);
        Window window = model is PortLockWindowModel ? new PortLockWindow() : new ExplorerLockWindow();
        var declaredMinHeight = window.MinHeight;
        window.Width = width;
        window.Height = height;
        window.DataContext = model;
        window.ShowInTaskbar = false;
        Window? activationFixture = null;

        window.Opened += async (_, _) =>
        {
            try
            {
                var load = PreviewScenario.LoadAsync(model, scenario);
                if (!scenario.Equals("loading", StringComparison.OrdinalIgnoreCase))
                    await load;
                window.Height = height;
                await Task.Delay(180);
                var selection = ReadOption(args, "--selection") ?? "first";
                model.SelectedItem = selection switch
                {
                    "none" => null,
                    "protected" => model.Items.FirstOrDefault(item => item.IsProtected),
                    "long" => model.Items.ElementAtOrDefault(2),
                    _ => model.Items.FirstOrDefault()
                };

                var table = window.GetVisualDescendants().OfType<DataGrid>().Single();
                if (model.SelectedItem is not null)
                    table.ScrollIntoView(model.SelectedItem, null);
                window.Activate();
                var focusMode = ReadOption(args, "--focus") ?? "focused";
                var focused = !focusMode.Equals("unfocused", StringComparison.OrdinalIgnoreCase);
                if (focused)
                    table.Focus(NavigationMethod.Tab);
                else
                    window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "查询") && button.IsEnabled).Focus(NavigationMethod.Tab);
                if (focusMode.Equals("inactive", StringComparison.OrdinalIgnoreCase))
                {
                    activationFixture = new Window
                    {
                        Title = "Preview focus fixture",
                        Width = 160,
                        Height = 80,
                        ShowInTaskbar = false,
                        Content = new TextBlock { Text = "Preview focus fixture", Margin = new Thickness(8) }
                    };
                    activationFixture.Show();
                    activationFixture.Activate();
                }
                await Task.Delay(220);
                window.UpdateLayout();

                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                SaveVisual(window, output);

                var tableFocus = table.IsKeyboardFocusWithin;
                var windowActive = window.IsActive;
                var rows = table.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
                var visibleRows = rows.Count(row =>
                {
                    var position = row.TranslatePoint(default, table);
                    return position is { } point && row.Bounds.Height > 0 && point.Y >= table.ColumnHeaderHeight - 0.5
                        && point.Y + row.Bounds.Height <= table.Bounds.Height + 0.5;
                });
                var selectedRow = rows.FirstOrDefault(row => ReferenceEquals(row.DataContext, model.SelectedItem));
                var tableBackground = ResourceColor(window, "ShadBackgroundBrush");
                var selectedBackground = tableBackground;
                if (selectedRow is not null)
                {
                    var selectedFill = selectedRow.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "BackgroundRectangle");
                    if (selectedFill?.Background is ISolidColorBrush fill)
                        selectedBackground = Blend(fill.Color, tableBackground, selectedFill.Opacity * fill.Opacity);
                }
                var selectedText = selectedRow?.GetVisualDescendants().OfType<TextBlock>().ToArray() ?? [];
                var detailPane = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DetailsPane");
                var detailText = detailPane.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsVisible && text.FindAncestorOfType<Button>() is null && !string.IsNullOrWhiteSpace(text.Text)).ToArray();
                var selectedContrast = MinimumContrast(selectedText, selectedBackground);
                var detailContrast = MinimumContrast(detailText, ResourceColor(window, "ShadCardBrush"));
                var endButton = detailPane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "结束进程"));
                var actionForeground = ((ISolidColorBrush)endButton.Foreground!).Color;
                var actionBackground = ResourceColor(window, "ShadCardBrush");
                var actionFill = endButton.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_Background");
                if (actionFill?.Background is ISolidColorBrush actionBrush)
                    actionBackground = Blend(actionBrush.Color, actionBackground, actionFill.Opacity * actionBrush.Opacity);
                var actionContrast = Contrast(actionForeground, actionBackground);
                var actionReadable = !endButton.IsEffectivelyEnabled || actionContrast >= 4.5;
                var bodyText = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => IsRendered(text) && text.FindAncestorOfType<Button>() is null && !string.IsNullOrWhiteSpace(text.Text));
                var bodyContrasts = bodyText.Select(text =>
                {
                    var row = text.FindAncestorOfType<DataGridRow>();
                    var background = row is not null ? RowBackground(row, tableBackground)
                        : text.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, detailPane))
                            ? ResourceColor(window, "ShadCardBrush") : tableBackground;
                    return MinimumContrast([text], background);
                }).Where(value => value is not null).Select(value => value!.Value).ToArray();
                var bodyMinimum = bodyContrasts.Length == 0 ? (double?)null : bodyContrasts.Min();
                var enabledButtonContrasts = window.GetVisualDescendants().OfType<Button>()
                    .Where(button => IsRendered(button) && button.IsEffectivelyEnabled && button.Content is string)
                    .Select(button => ButtonContrast(button, button.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, detailPane))
                        ? ResourceColor(window, "ShadCardBrush") : tableBackground)).ToArray();
                var buttonsMinimum = enabledButtonContrasts.Length == 0 ? (double?)null : enabledButtonContrasts.Min();
                var inputContrasts = window.GetVisualDescendants().OfType<TextBox>().Where(input => IsRendered(input) && input.IsEffectivelyEnabled)
                    .Select(input => Contrast(((ISolidColorBrush)input.Foreground!).Color,
                        input.Background is ISolidColorBrush brush ? Blend(brush.Color, tableBackground, brush.Opacity) : tableBackground)).ToArray();
                var inputMinimum = inputContrasts.Length == 0 ? (double?)null : inputContrasts.Min();
                var content = (Control)window.Content!;
                var titlebar = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "PART_TitleBar");

                bool? menuBound = null;
                var more = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, "更多"));
                if (more?.Flyout is MenuFlyout menu)
                {
                    menu.ShowAt(more);
                    await Task.Delay(60);
                    menuBound = menu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item.Command, model.KillAllCommand));
                    menu.Hide();
                }
                var removeCommandsBound = true;
                var renderedTargetCount = 0;
                var manageTargets = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, "管理目标"));
                if (manageTargets?.Flyout is Flyout targetFlyout)
                {
                    targetFlyout.ShowAt(manageTargets);
                    await Task.Delay(100);
                    if (targetFlyout.Content is Control targetContent)
                    {
                        var removeButtons = targetContent.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "移除")).ToArray();
                        renderedTargetCount = removeButtons.Length;
                        removeCommandsBound = removeButtons.All(button => button.Command is not null);
                    }
                    targetFlyout.Hide();
                }
                var diagnosticsButton = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, "查看诊断"));
                var diagnosticItems = 0;
                double? diagnosticContrast = null;
                if (model.HasDiagnostics && diagnosticsButton?.Flyout is Flyout diagnosticsFlyout)
                {
                    diagnosticsFlyout.ShowAt(diagnosticsButton);
                    await Task.Delay(180);
                    if (diagnosticsFlyout.Content is Control popupContent)
                    {
                        diagnosticItems = popupContent.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault()?.ItemCount ?? 0;
                        var presenter = popupContent.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => control.GetType().Name == "FlyoutPresenter");
                        if (presenter is not null)
                        {
                            SaveVisual(presenter, Path.ChangeExtension(output, ".diagnostics.png"));
                            var popupBackground = presenter.GetVisualDescendants().OfType<Border>()
                                .FirstOrDefault(border => border.Background is ISolidColorBrush && border.Opacity > 0)?.Background as ISolidColorBrush;
                            diagnosticContrast = MinimumContrast(popupContent.GetVisualDescendants().OfType<TextBlock>(),
                                popupBackground?.Color ?? ResourceColor(window, "ShadPopoverBrush"));
                        }
                    }
                    diagnosticsFlyout.Hide();
                }

                var foregroundSamples = selectedText.Select(text => (text.Foreground as ISolidColorBrush)?.Color.ToString()).Distinct().ToArray();
                bool? feedbackBound = null;
                if (model.HasOperationFeedback)
                {
                    var feedbackButton = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "操作结果"));
                    if (feedbackButton.Flyout is Flyout feedbackFlyout)
                    {
                        feedbackFlyout.ShowAt(feedbackButton);
                        await Task.Delay(100);
                        if (feedbackFlyout.Content is Control feedbackContent)
                        {
                            feedbackBound = feedbackContent.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == model.OperationFeedback);
                            var presenter = feedbackContent.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => control.GetType().Name == "FlyoutPresenter");
                            if (presenter is not null) SaveVisual(presenter, Path.ChangeExtension(output, ".feedback.png"));
                        }
                        feedbackFlyout.Hide();
                    }
                }
                var globalContrast = new[] { selectedContrast, detailContrast, bodyMinimum, buttonsMinimum, diagnosticContrast, inputMinimum }
                    .Where(value => value is not null).Min();
                await File.WriteAllTextAsync(output + ".json", JsonSerializer.Serialize(new
                {
                    Artifact = "Own fake-data ShadWindow visual-tree render; no desktop capture or host-plugin validation",
                    ContrastScope = "Visible body text, enabled text buttons, text inputs, and diagnostic popup; raw sRGB ratios. Disabled buttons and non-text icons excluded.",
                    Scenario = scenario,
                    Palette = ShadTheme.GetInstance().ShadColorTheme.Name,
                    BaseTheme = window.ActualThemeVariant.ToString(),
                    RequestedSize = new { Width = width, Height = height },
                    ExactRequestedBounds = Math.Abs(window.Bounds.Width - width) < 0.5 && Math.Abs(window.Bounds.Height - height) < 0.5,
                    ProductDeclaredMinHeight = declaredMinHeight,
                    RequestedHeightDifference = window.Bounds.Height - height,
                    RenderedBounds = new { window.Bounds.Width, window.Bounds.Height },
                    ClientSize = new { window.ClientSize.Width, window.ClientSize.Height },
                    TitlebarIncluded = titlebar is not null,
                    TitlebarHeight = titlebar?.Bounds.Height,
                    ContentHeight = content.Bounds.Height,
                    TableBounds = BoundsInWindow(table, window),
                    TableMinimumHeight = table.MinHeight,
                    TablePaneBounds = BoundsInWindow((Control)table.Parent!, window),
                    ResultsViewBounds = BoundsInWindow(table.FindAncestorOfType<ProcessListUserControl>()!, window),
                    ResultsGridBounds = BoundsInWindow((Control)table.FindAncestorOfType<ProcessListUserControl>()!.Content!, window),
                    DetailBounds = BoundsInWindow(detailPane, window),
                    FeedbackEntryBounds = BoundsInWindow(window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "操作结果")), window),
                    DetailsPlacement = window.Bounds.Width < 820 ? "below" : "right",
                    State = model.QueryState.ToString(),
                    model.ItemCount,
                    FullyVisibleProcessRows = visibleRows,
                    EightRowsAtDefaultSize = width < 820 || !model.HasItems || visibleRows >= 8,
                    RequestedFocus = focusMode,
                    WindowIsActive = windowActive,
                    InactiveWindowConfirmed = !focusMode.Equals("inactive", StringComparison.OrdinalIgnoreCase) || !windowActive,
                    ActualTableKeyboardFocus = tableFocus,
                    SelectedProcess = model.SelectedItem?.ProcessName,
                    SelectedProtected = model.SelectedItem?.IsProtected,
                    CanTerminateSelected = model.KillSelectedCommand.CanExecute(null),
                    SelectedTextForegrounds = foregroundSamples,
                    SelectedRowBackground = selectedBackground.ToString(),
                    SelectedTextMinimumContrast = selectedContrast,
                    DetailTextMinimumContrast = detailContrast,
                    EndProcessButtonEnabled = endButton.IsEffectivelyEnabled,
                    EndProcessButtonForeground = actionForeground.ToString(),
                    EndProcessButtonBackground = actionBackground.ToString(),
                    EndProcessButtonContrast = actionContrast,
                    ReadableActionButton = actionReadable,
                    BodyTextMinimumContrast = bodyMinimum,
                    EnabledButtonMinimumContrast = buttonsMinimum,
                    TextInputMinimumContrast = inputMinimum,
                    DiagnosticTextMinimumContrast = diagnosticContrast,
                    GlobalMinimumTextContrast = globalContrast,
                    ReadableText = actionReadable && globalContrast >= 4.5,
                    KillAllMenuCommandBound = menuBound,
                    RenderedTargetCount = renderedTargetCount,
                    RemoveTargetCommandsBound = removeCommandsBound,
                    OperationFeedbackPopupBound = feedbackBound,
                    DiagnosticsCount = model.Diagnostics.Count,
                    RenderedDiagnosticItems = diagnosticItems,
                    model.HasFailures,
                    model.HasCoverageLimitations
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(output + ".error.txt")) File.Delete(output + ".error.txt");
                lifetime.Shutdown();
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output + ".error.txt", exception.ToString());
                lifetime.Shutdown(1);
            }
            finally
            {
                model.Dispose();
                activationFixture?.Close();
            }
        };
        return window;
    }

    private static void SaveVisual(Control control, string output)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(control);
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static Color ResourceColor(Control control, string key) =>
        ((ISolidColorBrush)control.FindResource(control.ActualThemeVariant, key)!).Color;

    private static bool IsRendered(Visual visual) => visual.IsVisible && visual.Opacity > 0
        && visual.GetVisualAncestors().All(ancestor => ancestor.IsVisible && ancestor.Opacity > 0);

    private static object BoundsInWindow(Control control, Window window)
    {
        var position = control.TranslatePoint(default, window);
        return new { X = position?.X, Y = position?.Y, control.Bounds.Width, control.Bounds.Height };
    }

    private static Color RowBackground(DataGridRow row, Color background)
    {
        var fill = row.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "BackgroundRectangle");
        return fill?.Background is ISolidColorBrush brush ? Blend(brush.Color, background, fill.Opacity * brush.Opacity) : background;
    }

    private static double ButtonContrast(Button button, Color background)
    {
        var fill = button.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "PART_Background");
        if (fill?.Background is ISolidColorBrush brush)
            background = Blend(brush.Color, background, fill.Opacity * brush.Opacity);
        return Contrast(((ISolidColorBrush)button.Foreground!).Color, background);
    }

    private static double? MinimumContrast(IEnumerable<TextBlock> texts, Color background)
    {
        var values = texts.Where(text => text.Foreground is ISolidColorBrush)
            .Select(text =>
            {
                var foreground = (ISolidColorBrush)text.Foreground!;
                return Contrast(Blend(foreground.Color, background, foreground.Opacity * text.Opacity), background);
            }).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static Color Blend(Color foreground, Color background, double opacity)
    {
        var alpha = foreground.A / 255d * opacity;
        return Color.FromRgb((byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return Linear(color.R) * 0.2126 + Linear(color.G) * 0.7152 + Linear(color.B) * 0.0722;
        }
        var front = Luminance(foreground);
        var back = Luminance(background);
        return (Math.Max(front, back) + 0.05) / (Math.Min(front, back) + 0.05);
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ReadSize(string[] args, string name, int fallback, int minimum) =>
        int.TryParse(ReadOption(args, name), out var value) ? Math.Max(minimum, value) : fallback;
}
