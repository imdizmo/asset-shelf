using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AssetShelf;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace(Avalonia.Logging.LogEventLevel.Warning).StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Look.LookStyles.Apply(this);
        Resources["MenuFlyoutPresenterBackground"] = Look.Theme.SurfaceRaised;
        Resources["MenuFlyoutPresenterBorderBrush"] = Look.Theme.BorderStrong;
        Resources["MenuFlyoutItemBackgroundPointerOver"] = Look.Theme.SurfaceActive;
        Resources["ControlContentThemeFontSize"] = Look.Theme.Body;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];
            int i = Array.IndexOf(args, "--settings");
            if (i >= 0 && i + 1 < args.Length)
            {
                Settings.FilePath = args[i + 1];
            }

            Settings settings = Settings.Load();
            bool debug = args.Contains("--debug");
            Log.Write("app", $"start, settings {Settings.FilePath}");
            var splash = new Splash();
            desktop.MainWindow = splash;
            splash.Show();
            var progress = new Progress<(int Done, int Total)>(p =>
                splash.Show($"Reading packs  {p.Done:N0} of {p.Total:N0}", p.Total == 0 ? null : 0.9 * p.Done / p.Total));
            _ = Task.Run(() => Library.Load(settings.Packs, settings.Index, progress)).ContinueWith(t =>
            {
                splash.Show("Building the grid", 0.95);
                // let the splash draw that line before the main window takes the UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!t.IsCompletedSuccessfully)
                    {
                        Log.Write("library", "read failed: " + t.Exception?.GetBaseException().Message);
                    }

                    MainWindow main;
                    using (Log.Time("app", "main window built"))
                    {
                        main = new MainWindow(settings, t.IsCompletedSuccessfully ? t.Result : new Library());
                    }

                    desktop.MainWindow = main;
                    main.Show();
                    splash.Close();
                    if (debug)
                    {
                        LogWindow.Open(main);
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
