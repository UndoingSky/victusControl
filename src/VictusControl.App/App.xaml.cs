using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VictusControl.App;

public partial class App : Application {

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VictusControl", "crash.log");

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        // A hardware control app that dies silently is impossible to support.
        // Record what happened, tell the user where, and keep going if we can.
        DispatcherUnhandledException += (_, args) => {
            Record(args.Exception, "dispatcher");
            MessageBox.Show(
                $"Victus Control hit an unexpected error:\n\n{args.Exception.Message}\n\nDetails written to:\n{LogPath}",
                "Victus Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception ex) Record(ex, "domain");
        };

        TaskScheduler.UnobservedTaskException += (_, args) => {
            Record(args.Exception, "task");
            args.SetObserved();
        };
    }

    private static void Record(Exception ex, string source) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"=== {DateTime.Now:u} [{source}] ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        } catch {
            // nothing useful left to do
        }
    }
}
