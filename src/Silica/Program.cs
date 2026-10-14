namespace Silica;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The sensor host runs without any UI, so it never loads WPF.
        if (args is ["--sensor-host", var pipe, var parent] && int.TryParse(parent, out var parentId))
            return Core.Sensors.SensorHost.Run(pipe, parentId);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
