using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemiconductorCsvAnalyzer.Models;
using SemiconductorCsvAnalyzer.Services;

namespace SemiconductorCsvAnalyzer;

public partial class ProductEditorWindow : Window
{
    public ManagedProduct Product { get; private set; }
    private readonly ObservableCollection<ProductTestStage> _flow;
    public ProductEditorWindow(ManagedProduct? product = null)
    {
        InitializeComponent();
        Product = product == null ? new() : ProductCatalogStore.Clone(product);
        if (product == null)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            Product.ConfigText = File.Exists(path) ? File.ReadAllText(path) : ConfigLoader.Serialize(new AnalyzerConfig());
            Product.Flow.Add(new());
        }
        _flow = new(Product.Flow);
        DgFlow.ItemsSource = _flow;
        TxtName.Text = Product.Name; TxtDescription.Text = Product.Description;
        TxtConfig.Text = Product.ConfigText; TxtUuid.Text = Product.UuidTestNumbers;
        HasFt.IsChecked = _flow.Any(s => s.Kind == "FT"); IncludePartial.IsChecked = Product.IncludePartialPass;
        FirstMode.IsChecked = Product.RetestMode == RetestMode.First;
        PreferGoodRetest.IsChecked = Product.PreferGoodRetest;
        FirstMode.IsEnabled = Product.PreferGoodRetest == false;
        if (Product.PreferGoodRetest) LastMode.IsChecked = true;
    }
    private void PreferGoodRetest_Click(object sender, RoutedEventArgs e)
    {
        LastMode.IsChecked = true;
        FirstMode.IsEnabled = PreferGoodRetest.IsChecked != true;
    }
    private void Add(string kind)
    {
        int count = _flow.Count(s => s.Kind == kind);
        _flow.Add(new() { Kind = kind, Name = count == 0 ? "常温" : count == 1 ? "高温" : count == 2 ? "低温" : "步骤 " + (count + 1), Temperature = count == 1 ? 85 : count == 2 ? -40 : 25 });
        HasFt.IsChecked = _flow.Any(s => s.Kind == "FT");
    }
    private void AddCp_Click(object sender, RoutedEventArgs e) => Add("CP");
    private void AddFt_Click(object sender, RoutedEventArgs e) => Add("FT");
    private void HasFt_Click(object sender, RoutedEventArgs e)
    {
        if (HasFt.IsChecked == true && !_flow.Any(s => s.Kind == "FT")) Add("FT");
        else if (HasFt.IsChecked != true) foreach (var row in _flow.Where(s => s.Kind == "FT").ToArray()) _flow.Remove(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DgFlow.SelectedItem is ProductTestStage row) _flow.Remove(row);
        HasFt.IsChecked = _flow.Any(s => s.Kind == "FT");
    }
    private void Move(int delta)
    {
        int from = DgFlow.SelectedIndex, to = from + delta;
        if (from >= 0 && to >= 0 && to < _flow.Count) _flow.Move(from, to);
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void LoadConfig_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Filter = "INI 配置|*.ini|所有文件|*.*" };
        if (file.ShowDialog() != true) return;
        try { var config = ConfigLoader.Load(file.FileName); TxtConfig.Text = ConfigLoader.Serialize(config); }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!DgFlow.CommitEdit(DataGridEditingUnit.Cell, true) || !DgFlow.CommitEdit(DataGridEditingUnit.Row, true))
                throw new InvalidDataException("请先修正流程中的温度输入。");
            Product.Name = TxtName.Text.Trim(); Product.Description = TxtDescription.Text.Trim();
            Product.ConfigText = TxtConfig.Text; Product.UuidTestNumbers = string.Join(",", RetestIdentityService.ParseTestNumbers(TxtUuid.Text));
            Product.IncludePartialPass = IncludePartial.IsChecked == true;
            Product.PreferGoodRetest = PreferGoodRetest.IsChecked == true;
            Product.RetestMode = FirstMode.IsChecked == true ? RetestMode.First : RetestMode.Last;
            Product.Flow = _flow.ToList();
            if (Product.Files.Any(f => !Product.Flow.Any(s => s.Id == f.StageId) &&
                !(f.UseSpecifiedYield && f.StageId == "COMBINED:" + f.SpecifiedKind && Product.Flow.Any(s => s.Kind == f.SpecifiedKind))))
                throw new InvalidDataException("该步骤已有登记文件，请先移除相应登记后再删除步骤。");
            ProductYieldService.Validate(Product); DialogResult = true;
        }
        catch (Exception ex) { TxtError.Text = ex.Message; }
    }
}
