using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MioKit.LockBreaker.Preview.Views;
using Ti.Avalonia.Shadcn;
using Ti.Avalonia.Shadcn.ColorTheme;

namespace MioKit.LockBreaker.Preview;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var theme = ShadTheme.GetInstance(this);
            var paletteIndex = Array.FindIndex(args, argument => argument.Equals("--palette", StringComparison.OrdinalIgnoreCase));
            theme.ShadColorTheme = paletteIndex >= 0 && paletteIndex + 1 < args.Length && args[paletteIndex + 1].Equals("blue", StringComparison.OrdinalIgnoreCase)
                ? ShadColorThemes.Blue : ShadColorThemes.Rose;
            if (args.Contains("--dark", StringComparer.OrdinalIgnoreCase))
                theme.ActiveBaseTheme = ThemeVariant.Dark;
            else
                theme.ActiveBaseTheme = ThemeVariant.Light;

            var scenarioIndex = Array.FindIndex(args, argument => argument.Equals("--scenario", StringComparison.OrdinalIgnoreCase));
            var scenario = scenarioIndex >= 0 && scenarioIndex + 1 < args.Length
                ? args[scenarioIndex + 1]
                : "files";
            desktop.MainWindow = PreviewRender.HasOutput(args)
                ? PreviewRender.CreateWindow(args, scenario, desktop)
                : args.Contains("--gallery", StringComparer.OrdinalIgnoreCase)
                ? new MainWindow()
                : PreviewScenario.CreateWindow(scenario);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
