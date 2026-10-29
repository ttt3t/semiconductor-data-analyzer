using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Compiled once per header edit, never once per row. Independent of WPF.</summary>
public sealed class ColumnSearchRule
{
    public const string Help = "勾选后搜索。数字：X、>X、<X、X..Y（含边界）、%X（整数整除）、?（一位数字）、NAN / ***（无有效数值）、!NAN / !***（有效数值）。文本：关键字、*、?、[abc]。";
    private enum Kind { All, Text, Pattern, Equal, Greater, GreaterOrEqual, Less, LessOrEqual, NotEqual, Range, Modulo, Missing, Present, Invalid }
    private Kind _kind;
    private string _text = "";
    private double _first, _last;
    private Regex? _pattern;
    private bool _numeric;
    public string? Error { get; private set; }

    public static ColumnSearchRule Compile(string? query, bool numeric)
    {
        string text = query?.Trim() ?? "";
        var rule = new ColumnSearchRule { _text = text, _numeric = numeric };
        if (text.Length == 0) return rule;
        if (numeric)
        {
            if (text.Equals("NAN", StringComparison.OrdinalIgnoreCase) || text == "***")
            { rule._kind = Kind.Missing; return rule; }
            if (text.Equals("!NAN", StringComparison.OrdinalIgnoreCase) || text == "!***")
            { rule._kind = Kind.Present; return rule; }
            int range = text.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                if (!Number(text[..range], out rule._first) || !Number(text[(range + 2)..], out rule._last) || rule._first > rule._last)
                    return rule.Invalid("范围应写为 X..Y，且 X ≤ Y。");
                rule._kind = Kind.Range; return rule;
            }
            if (text[0] == '%')
            {
                if (!Number(text[1..], out rule._first) || rule._first == 0 || rule._first != Math.Truncate(rule._first))
                    return rule.Invalid("%X 的 X 必须为非零整数。");
                rule._first = Math.Abs(rule._first); rule._kind = Kind.Modulo; return rule;
            }
            foreach (var (prefix, kind) in new[] { (">=", Kind.GreaterOrEqual), ("<=", Kind.LessOrEqual),
                ("!=", Kind.NotEqual), ("<>", Kind.NotEqual), (">", Kind.Greater), ("<", Kind.Less), ("=", Kind.Equal) })
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (!Number(text[prefix.Length..], out rule._first)) return rule.Invalid("比较符后需要有效数字。");
                    rule._kind = kind; return rule;
                }
            if (Number(text, out rule._first)) { rule._kind = Kind.Equal; return rule; }
        }
        if (text.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
        {
            var pattern = new StringBuilder("\\A");
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '*') pattern.Append(numeric ? "[0-9]*" : ".*");
                else if (c == '?') pattern.Append(numeric ? "[0-9]" : ".");
                else if (c == '[')
                {
                    int end = text.IndexOf(']', i + 1);
                    if (end < 0 || end == i + 1) return rule.Invalid("通配字符集应写为 [abc] 或 [a-z]。");
                    pattern.Append('[');
                    for (int j = i + 1; j < end; j++)
                    {
                        char member = text[j];
                        // Escape regex syntax; permit a hyphen only inside a range.
                        if (member == '-' && j > i + 1 && j < end - 1) pattern.Append('-');
                        else pattern.Append(Regex.Escape(member.ToString()));
                    }
                    pattern.Append(']'); i = end;
                }
                else pattern.Append(Regex.Escape(c.ToString()));
            }
            pattern.Append("\\z");
            try
            {
                rule._pattern = new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
                rule._kind = Kind.Pattern;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { return rule.Invalid("通配表达式无效或过长，请检查 [abc] / [a-z]。"); }
        }
        else rule._kind = Kind.Text;
        return rule;
    }

    private ColumnSearchRule Invalid(string error) { _kind = Kind.Invalid; Error = error; return this; }
    public static double? ParseNumber(string? text) => Number(text, out double number) ? number : null;
    private static bool Number(string? text, out double number) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number);

    public bool Matches(string text, double? number)
    {
        bool valid = number is double n && double.IsFinite(n);
        return _kind switch
        {
            Kind.All => true,
            Kind.Text => text.Contains(_text, StringComparison.OrdinalIgnoreCase),
            // A copied full test name containing literal brackets remains directly searchable.
            Kind.Pattern => (!_numeric && text.Contains(_text, StringComparison.OrdinalIgnoreCase)) ||
                ((!_numeric || valid) && _pattern!.IsMatch(text)),
            Kind.Missing => !valid,
            Kind.Present => valid,
            Kind.Equal => valid && number!.Value == _first,
            Kind.NotEqual => valid && number!.Value != _first,
            Kind.Greater => valid && number!.Value > _first,
            Kind.GreaterOrEqual => valid && number!.Value >= _first,
            Kind.Less => valid && number!.Value < _first,
            Kind.LessOrEqual => valid && number!.Value <= _first,
            Kind.Range => valid && number!.Value >= _first && number.Value <= _last,
            Kind.Modulo => valid && number!.Value == Math.Truncate(number.Value) && number.Value % _first == 0,
            _ => false
        };
    }
}
