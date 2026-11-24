using System.Diagnostics;
using System.IO;
using System.Windows;
using Ets2u.Core;
using Ets2u.Core.Update;

namespace Ets2u.App;

public partial class App : Application
{
    /// <summary>True when the app was started again by an update (shown once in the status bar).</summary>
    public static bool JustUpdated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length == 3 && args[0] == Updater.FinishArg && int.TryParse(args[2], out var pid))
        {
            // This is the new copy, unpacked in the update folder: put it in place of the old one and start that.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var target = args[1];
            var problem = Updater.Finish(AppContext.BaseDirectory, target, pid);
            if (problem is not null)
                MessageBox.Show("The update couldn't replace the app's files:\n\n" + problem +
                                "\n\nThe app you had is unchanged. You can download the new version from GitHub instead.",
                                "ETS2 Utilities", MessageBoxButton.OK, MessageBoxImage.Warning);
            try
            {
                var info = new ProcessStartInfo(Path.Combine(target, "ets2u.exe")) { UseShellExecute = false, WorkingDirectory = target };
                if (problem is null) info.ArgumentList.Add(Updater.UpdatedArg);
                Process.Start(info);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                AppDirs.Log("Starting the updated app: " + ex.Message);
            }
            Shutdown();
            return;
        }

        JustUpdated = args.Contains(Updater.UpdatedArg);
        Updater.CleanUp();
        new MainWindow().Show();
    }
}
