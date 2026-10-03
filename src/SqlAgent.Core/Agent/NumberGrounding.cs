using System.Globalization;
using System.Text.RegularExpressions;
using SqlAgent.Core.Execution;

namespace SqlAgent.Core.Agent;

/// <summary>
/// Checks that the figures an answer states come from the data: each number in the text must appear in some query
/// result of the turn (as a cell, inside a text cell, as a date part or as a row count) or in the user's own
/// question. Formatting does not matter ("1,234.5", "1.234,5", "1234.5"), nor does rounding ("1.2 million" for
/// 1,234,567) or a percentage sign. A figure the model computed itself (a sum, a difference) is not found and is
/// reported: the prompt tells the model to leave arithmetic to SQL.
/// </summary>
public static partial class NumberGrounding
{
    // A number not glued to a letter or digit before it (so "Q4", "AW2022" and "H2O" are not figures), with an optional
    // scale or percent suffix. Thousands separators are ',' '.' or a no-break space, in groups of exactly three digits.
    [GeneratedRegex(
        @"(?<![\p{L}\d.,])(?<num>\d{1,3}(?:[,.  ]\d{3}){1,5}(?:[.,]\d{1,18})?|\d{1,18}(?:[.,]\d{1,18})?)(?<suffix>%|[kKmMbB](?![\p{L}\d])|\s(?:thousand|million|billion|nghìn|ngàn|triệu|tỷ|tỉ)(?![\p{L}]))?(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FigurePattern();

    // "1. First", "2) Second": numbering of a list, not a figure.
    [GeneratedRegex(@"^\s*\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarkerPattern();

    private const decimal Epsilon = 0.000001m;

    private sealed record Figure(string Text, IReadOnlyList<(decimal Value, int Decimals)> Readings, decimal Scale, bool Percent);

    /// <summary>The figures of <paramref name="answer"/> found in neither the results nor <paramref name="question"/>.</summary>
    public static IReadOnlyList<string> UngroundedFigures(string answer, string question, IReadOnlyList<QueryResult> results)
    {
        var figures = ExtractFigures(answer);
        if (figures.Count == 0) return [];

        var known = KnownValues(question, results);
        return figures.Where(f => !known.Any(k => Matches(f, k))).Select(f => f.Text).Distinct().ToList();
    }

    /// <summary>Whether the text states any figure at all (list numbering aside).</summary>
    public static bool ContainsFigures(string text) => ExtractFigures(text).Count > 0;

    /// <summary>The figures of <paramref name="answer"/> that are not in <paramref name="question"/>.</summary>
    public static IReadOnlyList<string> FiguresNotInQuestion(string answer, string question)
    {
        var known = KnownValues(question, []);
        return ExtractFigures(answer).Where(f => !known.Any(k => Matches(f, k))).Select(f => f.Text).Distinct().ToList();
    }

    private static bool Matches(Figure figure, decimal known)
    {
        foreach (var (value, decimals) in figure.Readings)
        {
            // A figure is a rounding of the known value if it is within half a unit of its own last digit.
            try
            {
                var half = 0.5m * Pow10Negative(decimals) * figure.Scale;
                var scaled = value * figure.Scale;
                if (Math.Abs(scaled - known) <= half + Epsilon) return true;
                if (figure.Percent && Math.Abs(scaled - known * 100m) <= half + Epsilon) return true;
            }
            catch (OverflowException)
            {
                // An absurdly large figure cannot be a rounding of anything in the data.
            }
        }

        return false;
    }

    private static decimal Pow10Negative(int decimals)
    {
        var result = 1m;
        for (var i = 0; i < decimals; i++) result /= 10m;
        return result;
    }

    private static List<decimal> KnownValues(string question, IReadOnlyList<QueryResult> results)
    {
        var known = new List<decimal>();
        AddText(known, question);

        foreach (var result in results)
        {
            known.Add(result.RowCount);
            foreach (var row in result.Rows)
            {
                foreach (var cell in row) AddCell(known, cell);
            }
        }

        return known;
    }

    private static void AddCell(List<decimal> known, object? cell)
    {
        switch (cell)
        {
            case null:
                return;
            case string text:
                AddText(known, text);
                return;
            case DateTime dt:
                known.AddRange([dt.Year, dt.Month, dt.Day]);
                return;
            case DateTimeOffset dto:
                known.AddRange([dto.Year, dto.Month, dto.Day]);
                return;
            case DateOnly date:
                known.AddRange([date.Year, date.Month, date.Day]);
                return;
            case bool or byte[] or Guid:
                return;
            case double d when double.IsFinite(d) && Math.Abs(d) < 7.9e27:
                known.Add(Math.Abs((decimal)d));
                return;
            case float f when float.IsFinite(f) && Math.Abs(f) < 7.9e27f:
                known.Add(Math.Abs((decimal)f));
                return;
            case IConvertible convertible:
                try
                {
                    known.Add(Math.Abs(convertible.ToDecimal(CultureInfo.InvariantCulture)));
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    // Not a number (TimeSpan, a CLR type, ...): nothing to ground against.
                }

                return;
        }
    }

    private static void AddText(List<decimal> known, string text)
    {
        foreach (var figure in ExtractFigures(text, skipListMarkers: false))
            foreach (var (value, _) in figure.Readings)
            {
                if (value <= decimal.MaxValue / figure.Scale) known.Add(value * figure.Scale);
            }
    }

    private static List<Figure> ExtractFigures(string text, bool skipListMarkers = true)
    {
        var figures = new List<Figure>();
        foreach (var line in text.Split('\n'))
        {
            var body = line;
            if (skipListMarkers && ListMarkerPattern().Match(line) is { Success: true } marker) body = line[marker.Length..];

            foreach (Match match in FigurePattern().Matches(body))
            {
                var suffix = match.Groups["suffix"].Value.Trim();
                figures.Add(new Figure(
                    match.Value.Trim(),
                    Readings(match.Groups["num"].Value),
                    ScaleOf(suffix),
                    Percent: suffix == "%"));
            }
        }

        return figures;
    }

    private static decimal ScaleOf(string suffix) => suffix.ToLowerInvariant() switch
    {
        "k" or "thousand" or "nghìn" or "ngàn" => 1_000m,
        "m" or "million" or "triệu" => 1_000_000m,
        "b" or "billion" or "tỷ" or "tỉ" => 1_000_000_000m,
        _ => 1m,
    };

    /// <summary>
    /// The values a written number can mean. "1.234" is 1234 in some locales and 1.234 in others, so an ambiguous
    /// one yields both; "1.234,5" and "1,234.5" are not ambiguous (the last separator is the decimal point).
    /// </summary>
    private static List<(decimal Value, int Decimals)> Readings(string token)
    {
        var digitsOnly = (string s) => new string(s.Where(char.IsAsciiDigit).ToArray());
        var readings = new List<(decimal, int)>();

        var separators = token.Where(c => c is ',' or '.').ToList();
        var lastSeparator = token.LastIndexOfAny([',', '.']);
        var kinds = separators.Distinct().Count();

        if (separators.Count == 0 && !token.Any(c => c is ' ' or ' '))
        {
            Add(readings, digitsOnly(token), 0);
        }
        else if (kinds == 2)
        {
            Add(readings, digitsOnly(token[..lastSeparator]) + "." + digitsOnly(token[(lastSeparator + 1)..]), token.Length - lastSeparator - 1);
        }
        else if (separators.Count > 1 || token.Any(c => c is ' ' or ' '))
        {
            // Repeated separator or a no-break space: grouping only, unless the tail is not a group of three.
            var tail = token[(lastSeparator + 1)..];
            if (separators.Count > 1 && tail.Length != 3)
                Add(readings, digitsOnly(token[..lastSeparator]) + "." + digitsOnly(tail), tail.Length);
            else
                Add(readings, digitsOnly(token), 0);
        }
        else
        {
            var tail = token[(lastSeparator + 1)..];
            Add(readings, digitsOnly(token[..lastSeparator]) + "." + digitsOnly(tail), tail.Length);
            // Exactly three digits after a lone separator can be a thousands group ("1.234" or "1,234").
            if (tail.Length == 3 && lastSeparator is >= 1 and <= 3 && token[0] != '0') Add(readings, digitsOnly(token), 0);
        }

        return readings;

        static void Add(List<(decimal, int)> list, string number, int decimals)
        {
            if (decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                list.Add((value, decimals));
        }
    }
}
