namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Bounded import diagnostics: repeated malformed records never allocate a per-chip log.</summary>
internal sealed class StdfWarnings
{
    private readonly Dictionary<string, (string Message, int Count)> _messages = new(StringComparer.Ordinal);
    private int _omitted;

    public void Add(string key, string message)
    {
        if (_messages.TryGetValue(key, out var previous))
            _messages[key] = (previous.Message, previous.Count + 1);
        else if (_messages.Count < 40) _messages.Add(key, (message, 1));
        else _omitted++;
    }

    public void AddOnce(string key, string message)
    { if (!_messages.ContainsKey(key)) Add(key, message); }

    public string[] Messages => _messages.Values
        .Select(v => v.Message + (v.Count > 1 ? $"（共 {v.Count:N0} 次）" : ""))
        .Concat(_omitted > 0 ? new[] { $"另有 {_omitted:N0} 条警告未逐条列出。" } : Array.Empty<string>()).ToArray();

    public static string Combine(params string[] notices)
    {
        string text = string.Join("\n\n", notices.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
        return text.Length <= 24000 ? text : text[..24000] + "\n其余警告已省略，请分别打开原文件查看。";
    }
}
