using System.IO;
using System.Windows;
using System.Windows.Threading;
using Silica.Localization;
using Silica.Services;
using Silica.Views;
using Silica.Core.Sensors;

namespace Silica;

public partial class App : Application
{
    private const string InstanceName = "Silica.SingleInstance.7F3A";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;

    public static CommandLine Args { get; private set; } = new([]);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = new CommandLine(e.Args);

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task", a.Exception); a.SetObserved(); };

        Settings.Load();
        Settings.Persist = !Args.IsAutomation && Args.DumpStringsPath == null;
        Loc.Instance.Use(Args.Language ?? Settings.Current.Language);
        Silica.Core.Util.Fmt.Unit = Loc.Instance.T;
        Silica.Core.Util.Fmt.Text = Loc.Instance.T;
        Silica.Core.Util.Fmt.Plural = Loc.Instance.N;
        Loc.Instance.LanguageChanged += PageStore.Clear;

        if (Args.DumpStringsPath != null)
        {
            Shutdown(StringDump.Run(Args.DumpStringsPath));
            return;
        }

        if (Args.ReportPath != null)
        {
            var code = HeadlessReport.Run(Args.ReportPath);
            Shutdown(code);
            return;
        }

        // Ask for administrator rights once; if the prompt is declined, carry on in limited mode.
        if (!Elevation.IsElevated && !Args.IsAutomation && !Args.Restarted && !Args.NoElevate && Elevation.RestartElevated(e.Args, replacing: false))
        {
            Shutdown();
            return;
        }

        if (!Args.IsAutomation && !ClaimSingleInstance())
        {
            Shutdown();
            return;
        }

        ThemeService.Initialize();
        ApplySensorSettings();
        Settings.Changed += ApplySensorSettings;

        var window = new MainWindow();
        MainWindow = window;
        if (Args.IsAutomation) Automation.Attach(window, Args);
        else if ((Args.Minimized || Settings.Current.StartMinimized) && Settings.Current.TrayEnabled) window.StartHidden();
        else
        {
            if (Args.Minimized || Settings.Current.StartMinimized) window.WindowState = WindowState.Minimized;
            window.Show();
        }

        Monitoring.Start();
    }

    public static void ApplySensorSettings()
    {
        var s = Settings.Current;
        var hub = SensorHub.Shared;
        hub.HostLauncher ??= pipe => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ExecutablePath,
            $"--sensor-host {pipe} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
        hub.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.PollIntervalMs, 250, 10000));
        hub.IncludeVirtualNetwork = s.ShowVirtualNetwork;
        hub.Groups = s.SensorGroups;
        SensorFormat.Fahrenheit = s.Fahrenheit;
    }

    private bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(true, InstanceName, out var created);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Activate", out _);
        if (!created && Args.Restarted)
        {
            // The previous instance is shutting down to make room for this one.
            try { created = _instanceMutex.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created)
        {
            _activateSignal.Set();
            return false;
        }

        var signal = _activateSignal;
        var listener = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
        }) { IsBackground = true, Name = "Instance activation" };
        listener.Start();
        return true;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        e.Handled = true;
        if (Args.IsAutomation) return;
        ErrorDialog.Show(MainWindow, e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Monitoring.Stop();
        Settings.SaveNow();
        SensorHub.Shared.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Command-line switches. --report writes a report and exits; --shot is used for UI checks.</summary>
public sealed class CommandLine
{
    public CommandLine(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--report": ReportPath = Next(); break;
                case "--lang": Language = Next(); break;
                case "--minimized": Minimized = true; break;
                case "--restarted": Restarted = true; break;
                case "--no-elevate": NoElevate = true; break;
                case "--shot": ShotPath = Next(); break;
                case "--page": Page = Next(); break;
                case "--theme": Theme = Next(); break;
                case "--render": Render = Next(); break;
                case "--size": Size = Next(); break;
                case "--wait": Wait = double.TryParse(Next(), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 3; break;
                case "--search": Search = Next(); break;
                case "--dump-strings": DumpStringsPath = Next(); break;
            }
        }
        if (ReportPath != null) ReportPath = Path.GetFullPath(ReportPath);
    }

    public string? ReportPath { get; }
    public string? Language { get; }
    public bool Minimized { get; }
    public bool Restarted { get; }
    public bool NoElevate { get; }
    public string? ShotPath { get; }
    public string? Page { get; }
    public string? Theme { get; }
    public string? Render { get; }
    public string? Size { get; }
    public double Wait { get; } = 3;
    public string? Search { get; }
    public string? DumpStringsPath { get; }
    public bool IsAutomation => ShotPath != null;
}
