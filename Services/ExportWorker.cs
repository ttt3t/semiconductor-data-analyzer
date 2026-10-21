using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Rare workbook/rendering dependencies are loaded in a short-lived copy of the same EXE.</summary>
internal static class ExportWorker
{
    private sealed class Job
    {
        public int Version { get; set; } = DiskStorage.FormatVersion;
        public string Kind { get; set; } = "";
        public string Output { get; set; } = "";
        public string Result { get; set; } = "";
        public string SourceName { get; set; } = "";
        public DiskDatasetSnapshot? Dataset { get; set; }
        public AnalyzerConfig Config { get; set; } = new();
        public string[] Wafers { get; set; } = Array.Empty<string>();
        public RetestMode Mode { get; set; }
        public double? OutlierSigma { get; set; }
        public XlsxReportOptions? Report { get; set; }
        public ManagedProduct? Product { get; set; }
        public ManagementPeriod Period { get; set; }
        public string Filter { get; set; } = "";
        public int DataMemoryMiB { get; set; } = 256;
    }
    private sealed record Result(string? Error, FailChipExportResult? Fail);
    private static readonly SemaphoreSlim Gate = new(1);
    public static async Task<FailChipExportResult> SaveFailAsync(string path, CsvParseResult data, AnalyzerConfig config,
        string[] wafers, RetestMode mode, double? sigma)
    {
        if (!CanShare(data)) return await Task.Run(() => FailChipXlsxService.Save(path, data, config, wafers, mode, sigma));
        using var lease = new DatasetLease(data.Records);
        using var snapshot = await Task.Run(() => DiskDatasetSnapshot.Capture(data));
        var result = await RunAsync(new() { Kind = "fail", Output = path, Dataset = snapshot, Config = config, Wafers = wafers, Mode = mode, OutlierSigma = sigma }, AnalysisOperation.Token);
        return result.Fail ?? throw new IOException("导出进程未返回统计结果。");
    }
    public static async Task SaveReportAsync(string path, CsvParseResult data, AnalyzerConfig config, XlsxReportOptions options, string name)
    {
        if (!CanShare(data)) { await XlsxReportService.SaveAsync(path, data, config, options, name); return; }
        using var lease = new DatasetLease(data.Records);
        using var snapshot = await Task.Run(() => DiskDatasetSnapshot.Capture(data));
        await RunAsync(new() { Kind = "report", Output = path, Dataset = snapshot, Config = config, Report = options, SourceName = name }, AnalysisOperation.Token);
    }
    public static Task SaveManagementAsync(string path, ManagedProduct product, string filter, ManagementPeriod period, CancellationToken token) =>
        RunAsync(new() { Kind = "management", Output = path, Product = product, Filter = filter, Period = period }, token);
    private static bool CanShare(CsvParseResult data) => data.Records.IsOnDisk && data.Items.All(i => i.Values.IsDiskBacked);
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Assembly.Location is used only for the non-bundled dotnet DLL host; a bundled EXE starts Environment.ProcessPath directly.")]
    private static async Task<Result> RunAsync(Job job, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        string manifest = "", temporaryOutput = "";
        try
        {
            manifest = DiskStorage.NewPath(".job.json"); string output = Path.GetFullPath(job.Output);
            // Replace the destination only after the child has finished successfully.
            temporaryOutput = job.Output = output + "." + Guid.NewGuid().ToString("N") + ".xlsx";
            job.Result = DiskStorage.NewPath(".result.json"); job.DataMemoryMiB = UiPreferencesService.Current.Value.DataMemoryMiB;
            await using (var file = File.Create(manifest)) await JsonSerializer.SerializeAsync(file, job, cancellationToken: token);
            var start = new ProcessStartInfo { FileName = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。"), UseShellExecute = false, CreateNoWindow = true };
            if (Assembly.GetEntryAssembly() != Assembly.GetExecutingAssembly()) start.FileName = "dotnet";
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--storage-worker"); start.ArgumentList.Add(manifest);
            using var process = Process.Start(start) ?? throw new IOException("导出进程未能启动。");
            try { await process.WaitForExitAsync(token); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(job.Result)) throw new IOException($"导出进程意外退出（{process.ExitCode}），原目标文件已保留。");
            await using var resultFile = File.OpenRead(job.Result);
            var result = await JsonSerializer.DeserializeAsync<Result>(resultFile, cancellationToken: token) ?? throw new IOException("导出结果为空。");
            if (process.ExitCode != 0 || result.Error != null) throw new IOException(result.Error ?? "导出失败。");
            File.Move(job.Output, output, overwrite: true); return result;
        }
        finally
        {
            try
            {
                foreach (string file in new[] { manifest, job.Result, temporaryOutput }.Where(p => p.Length > 0))
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (temporaryOutput.Length > 0)
                    foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(temporaryOutput)!, Path.GetFileName(temporaryOutput) + ".*"))
                        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Gate.Release(); }
        }
    }
    internal static int Execute(string path)
    {
        Job? job = null;
        try
        {
            using (var manifest = File.OpenRead(path)) job = JsonSerializer.Deserialize<Job>(manifest) ?? throw new InvalidDataException("导出任务为空。");
            if (job.Version != DiskStorage.FormatVersion) throw new InvalidDataException("导出任务版本不匹配。");
            DiskStorage.ConfigureExportBudget(job.DataMemoryMiB);
            using var operation = new AnalysisOperation();
            FailChipExportResult? result = null;
            if (job.Kind == "management") ManagementReportService.SaveWorkbook(job.Output, job.Product!, job.Filter, default, job.Period);
            else
            {
                var data = job.Dataset!.Open();
                if (job.Kind == "fail") result = FailChipXlsxService.Save(job.Output, data, job.Config, job.Wafers, job.Mode, job.OutlierSigma);
                else if (job.Kind == "report") XlsxReportService.Save(job.Output, data, job.Config, job.Report!, job.SourceName);
                else throw new InvalidDataException("未知的导出任务。");
            }
            using var output = File.Create(job.Result); JsonSerializer.Serialize(output, new Result(null, result)); return 0;
        }
        catch (Exception ex)
        {
            if (job != null) { using var output = File.Create(job.Result); JsonSerializer.Serialize(output, new Result(ex.Message, null)); }
            return 1;
        }
    }
}
