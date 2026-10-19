using System.IO;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

public static class OpenFailureReport
{
    public static string Describe(Exception exception, IEnumerable<string> files, string? logDirectory = null)
    {
        string details = exception is AggregateException aggregate
            ? string.Join("\n", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct()) : exception.Message;
        string summary = "数据打开失败，未修改登记或原文件。\n\n原因：" + details + "\n\n所选文件：\n" + string.Join("\n", files);
        try
        {
            string directory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemiconductorCsvAnalyzer", "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "open-failure-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            File.WriteAllText(path, summary + "\n\n" + exception, new UTF8Encoding(false));
            summary += "\n\n详细错误已保存：" + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { summary += "\n\n错误日志未能写入：" + ex.Message; }
        return summary;
    }
}
