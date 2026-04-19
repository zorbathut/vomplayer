using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Vomplayer;

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
            var window = new MainWindow();
            var args = desktop.Args;
            if (args is { Length: > 0 })
            {
                window.InitialFile = args[0];
            }
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}