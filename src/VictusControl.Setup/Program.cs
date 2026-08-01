using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace VictusControl.Setup;

[SupportedOSPlatform("windows")]
internal static class Program {

    private const string AppName = "Victus Control";
    private const string AppExeName = "VictusControl.App.exe";

    [STAThread]
    private static int Main() {
        try {
            if (HasRunningApp()) {
                MessageBox.Show(
                    "Victus Control is currently running. Close it first, then run setup again.",
                    AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 1;
            }

            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
            InstallPayload(installDir);
            CreateShortcuts(installDir);

            MessageBox.Show(
                $"Victus Control was installed to:\n\n{installDir}\n\nA desktop shortcut and Start Menu entry were created.\nWindows does not allow installers to silently pin apps to Start, so you can pin it manually from the Start Menu shortcut.",
                AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return 0;
        } catch (Exception ex) {
            MessageBox.Show(
                $"Setup failed:\n\n{ex.Message}",
                AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool HasRunningApp() {
        return System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)).Length > 0;
    }

    private static void InstallPayload(string installDir) {
        if (Directory.Exists(installDir)) {
            Directory.Delete(installDir, recursive: true);
        }

        Directory.CreateDirectory(installDir);

        using Stream? payload = GetPayloadStream();
        if (payload is null) {
            throw new InvalidOperationException("Installer payload is missing.");
        }

        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries) {
            string destinationPath = Path.Combine(installDir, entry.FullName);

            if (string.IsNullOrEmpty(entry.Name)) {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static Stream? GetPayloadStream() {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("VictusControl.App.zip", StringComparison.OrdinalIgnoreCase));

        return resourceName is null ? null : assembly.GetManifestResourceStream(resourceName);
    }

    private static void CreateShortcuts(string installDir) {
        string targetPath = Path.Combine(installDir, AppExeName);
        string iconPath = targetPath;

        string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");
        string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName);
        string startMenuShortcut = Path.Combine(startMenuDir, AppName + ".lnk");

        Directory.CreateDirectory(Path.GetDirectoryName(desktopShortcut)!);
        Directory.CreateDirectory(startMenuDir);

        CreateShortcut(desktopShortcut, targetPath, installDir, iconPath);
        CreateShortcut(startMenuShortcut, targetPath, installDir, iconPath);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string iconPath) {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) {
            throw new InvalidOperationException("Windows Script Host is not available.");
        }

        object shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Unable to create shortcut shell.");
        dynamic shellCom = shell;
        dynamic shortcut = shellCom.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = iconPath;
        shortcut.Save();
    }
}