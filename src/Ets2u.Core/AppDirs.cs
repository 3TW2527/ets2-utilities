namespace Ets2u.Core;

/// <summary>The app's own folders.</summary>
public static class AppDirs
{
    public static string Settings => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ets2u");

    public static string Local => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ets2u");

    public static string Backups => Path.Combine(Local, "backups");
    public static string GameCache => Path.Combine(Local, "game");
    public static string LogFile => Path.Combine(Local, "log.txt");

    public static void Log(string message)
    {
        Directory.CreateDirectory(Local);
        File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }
}
