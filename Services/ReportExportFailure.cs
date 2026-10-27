using System.IO;
using System.Reflection;
using System.Text;

namespace SemiconductorCsvAnalyzer.Services;

public static class ReportExportFailure
{
    public static string Describe(Exception exception, string? logDirectory = null)
    {
        var summary = new StringBuilder("报告导出失败。\n\n原因：").Append(exception.Message);
        foreach (string key in new[] { "ReportStage", "ReportItem" })
            if (exception.Data[key] is string value && value.Length > 0)
                summary.Append('\n').Append(key == "ReportStage" ? "阶段：" : "测项：").Append(value);
        try
        {
            string directory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SemiconductorCsvAnalyzer", "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"report-failure-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
            var details = new StringBuilder(summary.ToString());
            details.Append("\n\nVersion: ").Append(typeof(ReportExportFailure).Assembly.GetName().Version)
                .Append("\nBuild: ").Append(typeof(ReportExportFailure).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
                .Append("\nOS: ").Append(Environment.OSVersion);
            if (exception.Data["ReportContext"] is string context) details.Append("\nContext: ").Append(context);
            details.Append("\n\n").Append(exception);
            File.WriteAllText(path, details.ToString(), new UTF8Encoding(false));
            summary.Append("\n\n详细日志：").Append(path).Append("\n请提供此日志以定位原因。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { summary.Append("\n\n日志未能写入：").Append(ex.Message); }
        return summary.ToString();
    }
}
