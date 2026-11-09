using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ProductManagementWindow
{
    private void YieldRow_Loading(object sender, DataGridRowEventArgs e)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += OpenYield_Click;
        var explore = new MenuItem { Header = "打开所在位置", ToolTip = "相同目录合并，每次最多打开 5 个位置。" };
        explore.Click += ExploreYield_Click;
        menu.Items.Add(open);
        menu.Items.Add(explore);
        menu.Opened += (_, _) => open.Header = DgYields.SelectedItems.Count > 1 ? "合并打开所选行" : "打开";
        e.Row.ContextMenu = menu;
    }

    private void YieldRow_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(DgYields, target) is DataGridRow row)
        {
            if (!row.IsSelected)
            {
                DgYields.SelectedItems.Clear();
                row.IsSelected = true;
                row.Focus();
            }
            e.Handled = true;
        }
    }

    private async Task<ManagedYieldRow[]?> CurrentYieldRowsAsync(object sender)
    {
        if (_product == null || _busy || _closed || sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.PlacementTarget is not DataGridRow container || container.Item is not ManagedYieldRow clicked) return null;
        var selected = DgYields.SelectedItems.OfType<ManagedYieldRow>().ToArray();
        if (!selected.Contains(clicked)) selected = new[] { clicked };
        var productId = _product.Id;
        var period = Period;
        var filter = TxtFilter.Text.Trim();
        if (ProductYieldService.IsCurrent(_product)) return selected;
        // Old snapshots have no source associations. Recompute before resolving;
        // never guess a month's files from the registration timestamp.
        if (!await CalculateAsync(productId) || _closed || _product?.Id != productId) return null;
        if (Period != period || TxtFilter.Text.Trim() != filter) return null;
        static (string, string, string, string, string, bool) Key(ManagedYieldRow row) =>
            (row.Kind, row.StageId, row.Lot, row.Wafer, row.Period, row.IsCombined);
        var current = ManagementTrendService.DetailYieldRows(_product, filter, period).ToLookup(Key);
        var resolved = new List<ManagedYieldRow>(selected.Length);
        foreach (var row in selected)
        {
            var matches = current[Key(row)].ToArray();
            if (matches.Length != 1)
            { TxtStatus.Text = "重算后部分行已变化，请重新选择良率明细。"; return null; }
            resolved.Add(matches[0]);
        }
        return resolved.ToArray();
    }

    private async void OpenYield_Click(object sender, RoutedEventArgs e)
    {
        var rows = await CurrentYieldRowsAsync(sender);
        if (rows == null || _product == null || _busy || _closed) return;
        var product = _product;
        long version = _version;
        MainWindow? opening = null;
        string[] paths = Array.Empty<string>();
        SetBusy(true);
        try
        {
            paths = ManagedAnalysisService.YieldFiles(product, rows).Select(f => f.FilePath).ToArray();
            var groups = ManagedAnalysisService.YieldSources(product, rows);
            foreach (var sources in groups)
            {
                if (_closed || version != _version || _product?.Id != product.Id) break;
                var config = ConfigLoader.Parse(sources[0].ConfigText, product.Name + ".ini");
                opening = new MainWindow();
                opening.Show();
                await opening.OpenManagedFilesAsync(sources, config, product.RetestMode);
                opening = null;
            }
            if (!_closed && version == _version) TxtStatus.Text = $"已打开所选 {rows.Length} 行关联的 {paths.Length} 条源文件登记，共 {groups.Length} 个分析窗口（同一步骤内合并）。";
        }
        catch (OperationCanceledException) { opening?.Close(); if (!_closed && version == _version) TxtStatus.Text = "已取消打开。"; }
        catch (Exception ex)
        {
            opening?.Close();
            if (!_closed && version == _version)
            {
                TxtStatus.Text = "打开失败：" + ex.Message;
                MessageBox.Show(this, OpenFailureReport.Describe(ex, paths), "数据打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { if (!_closed && version == _version) SetBusy(false); }
    }

    private async void ExploreYield_Click(object sender, RoutedEventArgs e)
    {
        var rows = await CurrentYieldRowsAsync(sender);
        if (rows == null || _product == null || _busy || _closed) return;
        string productId = _product.Id;
        long version = ++_version;
        SetBusy(true);
        try
        {
            var plan = ExplorerLocationPlan.Create(ManagedAnalysisService.YieldFiles(_product, rows).Select(f => f.FilePath));
            var targets = await Task.Run(() => plan.Files.Select(path =>
            {
                string directory = Path.GetDirectoryName(path)!;
                bool exists = File.Exists(path);
                return (Path: path, Directory: directory, Exists: exists, Valid: exists || Directory.Exists(directory));
            }).ToArray());
            if (_closed || version != _version || _product?.Id != productId) return;
            int opened = 0;
            var errors = plan.Errors.ToList();
            foreach (var target in targets)
            {
                try
                {
                    if (!target.Valid) throw new DirectoryNotFoundException("原目录不存在，请在数据文件页重新定位。");
                    using var process = Process.Start(new ProcessStartInfo("explorer.exe")
                    {
                        UseShellExecute = true,
                        Arguments = target.Exists ? "/select,\"" + target.Path + "\"" : "\"" + target.Directory + "\""
                    });
                    opened++;
                }
                catch (Exception ex) { errors.Add(target.Path + "：" + ex.Message); }
            }
            TxtStatus.Text = $"已请求打开 {opened} 个不同目录（相同位置已合并，最多 {ExplorerLocationPlan.WindowLimit} 个窗口）。";
            if (plan.TotalLocations > plan.Files.Length) TxtStatus.Text += $"另有 {plan.TotalLocations - plan.Files.Length} 个目录未打开，可缩小选择范围后定位。";
            if (plan.InvalidCount > 0) TxtStatus.Text += $"{plan.InvalidCount} 个路径无效。";
            if (errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, errors), "部分文件无法定位", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { if (!_closed && version == _version) TxtStatus.Text = "打开所在位置失败：" + ex.Message; }
        finally { if (!_closed && version == _version) SetBusy(false); }
    }
}
