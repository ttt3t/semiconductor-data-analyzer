using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemiconductorCsvAnalyzer.Models;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record SimulationScheme(int Version, string Signature, RetestMode Retest,
    PredictionOptions Prediction, SimulationRule[] Rules);

public static class SimulationSchemeService
{
    public static string Signature(CsvParseResult data, AnalyzerConfig config)
    {
        // Stable across reloads: IDs are intentionally not part of the identity.
        string definition = JsonSerializer.Serialize(new
        {
            Items = data.Items.Select(i => new { i.Name, i.TestNumber, i.Unit, i.LowLimit, i.HighLimit }),
            config.PassHBins, config.PartialPassHBins, config.Product, config.Program, config.Lot,
            config.ProductColumn, config.ProgramColumn, config.LotColumn, config.ProgramCompatibility,
            config.NotApplicableTokens, config.MissingTokens
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
    }
    public static void Save(string path, SimulationScheme scheme)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(scheme, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static SimulationScheme Load(string path, CsvParseResult data, AnalyzerConfig config)
    {
        var scheme = JsonSerializer.Deserialize<SimulationScheme>(File.ReadAllText(path))
            ?? throw new InvalidDataException("方案为空。");
        if (scheme.Version != 1 || scheme.Signature != Signature(data, config))
            throw new InvalidDataException("方案的测试项、原始阈值或 HBIN / 预测上下文配置与当前数据不匹配。");
        if (scheme.Rules == null || scheme.Prediction == null || scheme.Rules.Length != data.Items.Count ||
            scheme.Rules.Select(r => r.Item).Distinct().Count() != data.Items.Count ||
            scheme.Rules.Any(r => r.Item < 0 || r.Item >= data.Items.Count || r.Stage < 1 || r.Lower > r.Upper) ||
            scheme.Retest is not (RetestMode.First or RetestMode.Last)) throw new InvalidDataException("方案规则无效。");
        return scheme;
    }
}
