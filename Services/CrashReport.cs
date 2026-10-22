using System.IO;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

internal static class CrashReport
{
    internal static string Describe(Exception exception)
    {
        string details = $"Version: {typeof(CrashReport).Assembly.GetName().Version}\n" +
            $"Time: {DateTimeOffset.Now:O}\nOS: {Environment.OSVersion}\n" +
            $"Process: {Environment.ProcessPath}\nBase directory: {AppContext.BaseDirectory}\n\n{exception}";
        foreach (string directory in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorCsvAnalyzer", "Logs"),
            Path.Combine(Path.GetTempPath(), "SemiconductorCsvAnalyzer", "Logs") })
        {
            try
            {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
                File.WriteAllText(file, details, new UTF8Encoding(false));
                return "程序发生错误，无法继续运行。\n原有产品登记、配置和测试文件不会被重置。\n\n原因：" +
                    exception.GetBaseException().Message + "\n\n详细错误日志：\n" + file;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* Reporting must not hide the original failure. Try a writable fallback. */ }
        }
        return "程序发生错误：\n" + exception + "\n\n错误日志未能写入磁盘。";
    }
}
