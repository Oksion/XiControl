using System.Runtime.InteropServices;

namespace XiControl;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            StartupDiagnostics.Write("AppDomain.UnhandledException", args.ExceptionObject);

        try
        {
            InitializeWindowsAppRuntimeForSingleFile();
            XamlGeneratedProgram.XamlGeneratedMain();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Application.Start", ex);
            throw;
        }
    }

    /// <summary>
    /// Windows App SDK self-contained registration resolves native/WinRT classes relative to
    /// this directory. Version 2.3 also validates the owner PID so inherited environment from a
    /// parent process cannot redirect a child to foreign PRI/DLL files. Keep this before the first
    /// Microsoft.UI.Xaml access; it is required by the SDK's SingleFile target.
    /// </summary>
    private static void InitializeWindowsAppRuntimeForSingleFile()
    {
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = WindowsAppRuntimeEnsureIsLoaded();
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", EntryPoint = "WindowsAppRuntime_EnsureIsLoaded",
        ExactSpelling = true)]
    private static extern int WindowsAppRuntimeEnsureIsLoaded();
}

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();

    internal static void Write(string stage, object failure)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XiControl");
            Directory.CreateDirectory(directory);

            string details = failure is Exception exception ? exception.ToString() : failure.ToString() ?? "<null>";
            string entry = $"""
                {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {stage}
                Process: {Environment.ProcessPath}
                Runtime: {RuntimeInformation.FrameworkDescription}
                OS: {RuntimeInformation.OSDescription}
                {details}

                """;

            lock (Sync)
            {
                File.AppendAllText(Path.Combine(directory, "startup-crash.txt"), entry);
            }
        }
        catch
        {
            // Diagnostics must never replace the original startup failure.
        }
    }

}
