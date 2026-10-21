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
        var explore = new MenuItem { Header = "打开所在位置" };
        explore.Click += ExploreYield_Click;
        menu.Items.Add(open);
        menu.Items.Add(explore);
        e.Row.ContextMenu = menu;
    }

    private void YieldRow_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(DgYields, target) is DataGridRow row)
        {
            DgYields.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private async Task<ManagedYieldRow?> CurrentYieldRowAsync(object sender)
    {
        if (_product == null || _busy || _closed || sender is not MenuItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
            menu.PlacementTarget is not DataGridRow container || container.Item is not ManagedYieldRow selected) return null;
        var productId = _product.Id;
        var period = Period;
        var filter = TxtFilter.Text.Trim();
        if (ProductYieldService.IsCurrent(_product)) return selected;
        // Old snapshots have no source associations. Recompute before resolving;
        // never guess a month's files from the registration timestamp.
        if (!await CalculateAsync(productId) || _closed || _product?.Id != productId) return null;
        if (Period != period || TxtFilter.Text.Trim() != filter) return null;
        var current = ManagementTrendService.DetailYieldRows(_product, filter, period).FirstOrDefault(row =>
            row.Kind == selected.Kind && row.StageId == selected.StageId && row.Lot == selected.Lot &&
            row.Wafer == selected.Wafer && row.Period == selected.Period);
        if (current == null) TxtStatus.Text = "重算后该行已变化，请重新选择良率明细。";
        return current;
    }

    private async void OpenYield_Click(object sender, RoutedEventArgs e)
    {
        var row = await CurrentYieldRowAsync(sender);
        if (row == null || _product == null || _busy || _closed) return;
        var product = _product;
        long version = _version;
        MainWindow? opening = null;
        string[] paths = Array.Empty<string>();
        SetBusy(true);
        try
        {
            paths = ManagedAnalysisService.YieldFiles(product, row).Select(f => f.FilePath).ToArray();
            var groups = ManagedAnalysisService.YieldSources(product, row);
            foreach (var sources in groups)
            {
                if (_closed || version != _version || _product?.Id != product.Id) break;
                var config = ConfigLoader.Parse(sources[0].ConfigText, product.Name + ".ini");
                opening = new MainWindow { Owner = this };
                opening.Show();
                await opening.OpenManagedFilesAsync(sources, config, product.RetestMode);
                opening = null;
            }
            if (!_closed && version == _version) TxtStatus.Text = $"已打开 {paths.Length} 条源文件登记，共 {groups.Length} 个分析窗口（同一步骤内合并）。";
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
        var row = await CurrentYieldRowAsync(sender);
        if (row == null || _product == null || _busy || _closed) return;
        try
        {
            var files = ManagedAnalysisService.YieldFiles(_product, row).DistinctBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase);
            int opened = 0;
            var errors = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file.FilePath)) throw new FileNotFoundException("原文件不存在，请在数据文件页重新定位。", file.FilePath);
                    using var process = Process.Start(new ProcessStartInfo("explorer.exe")
                    {
                        UseShellExecute = true,
                        Arguments = "/n,/select,\"" + Path.GetFullPath(file.FilePath) + "\""
                    });
                    opened++;
                }
                catch (Exception ex) { errors.Add(file.FileName + "：" + ex.Message); }
            }
            TxtStatus.Text = $"已请求打开 {opened} 个资源管理器窗口。";
            if (errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, errors), "部分文件无法定位", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { TxtStatus.Text = "打开所在位置失败：" + ex.Message; }
    }
}
