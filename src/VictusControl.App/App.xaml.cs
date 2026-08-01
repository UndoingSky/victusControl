using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VictusControl.App;

public partial class App : Application {

    private const string MutexName = @"Local\VictusControl.App.SingleInstance";
    private const string PipeName = "VictusControl.App.Activate";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VictusControl", "crash.log");

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCancel;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        if (!TryBecomeSingleInstance()) {
            SignalPrimaryInstance();
            Shutdown();
            return;
        }

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

        MainWindow = new MainWindow();
        MainWindow.Show();
        BeginActivationListener();
    }

    protected override void OnExit(ExitEventArgs e) {
        _pipeCancel?.Cancel();
        _pipeCancel?.Dispose();
        _pipeCancel = null;

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private bool TryBecomeSingleInstance() {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        return createdNew;
    }

    private void BeginActivationListener() {
        _pipeCancel = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivation(_pipeCancel.Token));
    }

    private async Task ListenForActivation(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() => {
                    if (MainWindow is MainWindow window) {
                        window.RestoreFromTray();
                    }
                }, DispatcherPriority.Send);
            } catch (OperationCanceledException) {
                return;
            } catch {
                // A missed activation is better than crashing the running app.
            }
        }
    }

    private static void SignalPrimaryInstance() {
        for (int attempt = 0; attempt < 5; attempt++) {
            try {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);
                client.WriteByte(1);
                client.Flush();
                return;
            } catch {
                Thread.Sleep(100);
            }
        }
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
