using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Athena.UI.Services.Spreadsheets;

/// <summary>
/// Resolves cell style indexes to their number format so date/time serial numbers can be
/// reported as readable text. Excel stores dates as plain numbers, so without this map an
/// inspected cell looks like "45001" instead of "2023-03-15".
/// </summary>
internal sealed class XlsxNumberFormatMap
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly Dictionary<int, TemporalKind> BuiltInFormats = new()
    {
        [14] = TemporalKind.Date, [15] = TemporalKind.Date, [16] = TemporalKind.Date, [17] = TemporalKind.Date,
        [18] = TemporalKind.Time, [19] = TemporalKind.Time, [20] = TemporalKind.Time, [21] = TemporalKind.Time,
        [22] = TemporalKind.DateTime,
        [27] = TemporalKind.Date, [28] = TemporalKind.Date, [29] = TemporalKind.Date, [30] = TemporalKind.Date,
        [31] = TemporalKind.Date, [32] = TemporalKind.Time, [33] = TemporalKind.Time, [34] = TemporalKind.Time,
        [35] = TemporalKind.Time, [36] = TemporalKind.Date,
        [45] = TemporalKind.Time, [46] = TemporalKind.Time, [47] = TemporalKind.DateTime,
        [50] = TemporalKind.Date, [51] = TemporalKind.Date, [52] = TemporalKind.Date, [53] = TemporalKind.Date,
        [54] = TemporalKind.Date, [55] = TemporalKind.Date, [56] = TemporalKind.Date, [57] = TemporalKind.Date,
        [58] = TemporalKind.Date
    };

    private readonly Dictionary<int, TemporalKind> _styleKinds = new();
    private readonly bool _date1904;

    public XlsxNumberFormatMap(XDocument? styles, bool date1904)
    {
        _date1904 = date1904;
        if (styles?.Root is null) return;

        var customFormats = new Dictionary<int, string>();
        foreach (var format in styles.Root.Element(Ns + "numFmts")?.Elements(Ns + "numFmt") ?? [])
        {
            if (int.TryParse((string?)format.Attribute("numFmtId"), out var id))
                customFormats[id] = (string?)format.Attribute("formatCode") ?? string.Empty;
        }

        var styleIndex = 0;
        foreach (var cellFormat in styles.Root.Element(Ns + "cellXfs")?.Elements(Ns + "xf") ?? [])
        {
            if (int.TryParse((string?)cellFormat.Attribute("numFmtId"), out var numberFormatId))
            {
                var kind = customFormats.TryGetValue(numberFormatId, out var code)
                    ? ClassifyFormatCode(code)
                    : BuiltInFormats.GetValueOrDefault(numberFormatId, TemporalKind.None);
                if (kind != TemporalKind.None) _styleKinds[styleIndex] = kind;
            }
            styleIndex++;
        }
    }

    public bool IsTemporal(int? styleIndex) => styleIndex is not null && _styleKinds.ContainsKey(styleIndex.Value);

    /// <summary>Converts an Excel serial number into readable text, or null when the style is not temporal.</summary>
    public string? Format(int? styleIndex, double serial)
    {
        if (styleIndex is null || !_styleKinds.TryGetValue(styleIndex.Value, out var kind)) return null;
        if (serial is < 0 or > 2_958_465) return null;

        try
        {
            var moment = _date1904
                ? new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial)
                : DateTime.FromOADate(serial);
            return kind switch
            {
                TemporalKind.Date when moment.TimeOfDay == TimeSpan.Zero => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TemporalKind.Date => moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                TemporalKind.Time => moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                _ => moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classifies a format code by its date/time placeholders, ignoring literal text, colour
    /// blocks and escaped characters so that a currency code like "$#,##0" is never mistaken
    /// for a date because of its letters.
    /// </summary>
    private static TemporalKind ClassifyFormatCode(string code)
    {
        var meaningful = new StringBuilder(code.Length);
        var index = 0;
        while (index < code.Length)
        {
            var current = code[index];
            switch (current)
            {
                case '"':
                    index++;
                    while (index < code.Length && code[index] != '"') index++;
                    index++;
                    continue;
                case '[':
                    index++;
                    while (index < code.Length && code[index] != ']') index++;
                    index++;
                    continue;
                case '\\':
                    index += 2;
                    continue;
                case ';':
                    index = code.Length;
                    continue;
                default:
                    meaningful.Append(char.ToLowerInvariant(current));
                    index++;
                    continue;
            }
        }

        var text = meaningful.ToString();
        var hasDate = text.Contains('y') || text.Contains('d');
        var hasTime = text.Contains('h') || text.Contains('s');
        if (hasDate && hasTime) return TemporalKind.DateTime;
        if (hasDate) return TemporalKind.Date;
        if (hasTime) return TemporalKind.Time;
        return TemporalKind.None;
    }

    private enum TemporalKind
    {
        None,
        Date,
        Time,
        DateTime
    }
}
