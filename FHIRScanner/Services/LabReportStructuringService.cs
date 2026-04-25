using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FHIRScanner.Services;

public sealed class LabReportStructuringService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly Regex NumericValueRegex = new(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    private static readonly Regex FlagRegex = new(@"\[(?<flag>[A-Z]+)\]", RegexOptions.Compiled);
    private static readonly Regex SampleIdRegex = new(@"Sample\s*Id\s*:?\s*(?<value>[A-Za-z0-9\-\/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"(?<value>\d{1,2}:\d{2}:\d{2})", RegexOptions.Compiled);

    public LabReportStructuringResult Parse(OcrLayoutResult layout)
    {
        if (layout.Lines.Count == 0)
        {
            return new LabReportStructuringResult(false, null, "{}", "No OCR lines were available to structure.");
        }

        var report = new StructuredLabReport
        {
            Source = layout.Source,
        };

        var lines = layout.Lines
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .ToList();

        var lineOrdinals = lines
            .Select((line, index) => new { line, ordinal = index + 1 })
            .ToDictionary(item => item.line, item => item.ordinal);

        var wordsByLine = BuildWordsByLine(layout, lines);
        var titleLine = lines.FirstOrDefault(line => ContainsIgnoreCase(line.Text, "COMPLETE BLOOD COUNT"));
        if (titleLine is not null)
        {
            report.ReportTitle = CleanText(titleLine.Text);
        }

        report.PatientDisplayName = ExtractPatientDisplayName(lines, titleLine);
        report.HeaderFields.AddRange(ExtractHeaderFields(lines, titleLine, lineOrdinals));

        if (titleLine is null)
        {
            report.Warnings.Add("Could not find the report title line. Table parsing may be incomplete.");
        }

        var headerLine = lines.FirstOrDefault(line =>
            ContainsIgnoreCase(line.Text, "Test")
            && ContainsIgnoreCase(line.Text, "Result")
            && ContainsIgnoreCase(line.Text, "Unit"));

        var anchors = DetectColumnAnchors(layout, headerLine, wordsByLine);
        var currentSection = new LabReportSection
        {
            Name = report.ReportTitle ?? "Primary Panel",
        };
        report.Sections.Add(currentSection);

        var rowIndex = 1;
        var footerMode = false;

        foreach (var line in lines)
        {
            if (titleLine is not null && line.Top <= titleLine.Top)
            {
                continue;
            }

            if (headerLine is not null && ReferenceEquals(line, headerLine))
            {
                continue;
            }

            var text = CleanText(line.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!footerMode && IsFooterLine(text))
            {
                footerMode = true;
            }

            if (footerMode)
            {
                report.Notes.Add(text);
                continue;
            }

            if (IsSectionHeading(text))
            {
                currentSection = new LabReportSection
                {
                    Name = NormalizeHeading(text),
                };
                report.Sections.Add(currentSection);
                continue;
            }

            var lineWords = wordsByLine.TryGetValue(new LineReference(line), out var groupedWords)
                ? groupedWords
                : [];
            var columns = SplitIntoColumns(line, lineWords, anchors);

            if (ShouldAppendToPreviousRow(columns, currentSection))
            {
                var previousRow = currentSection.Rows[^1];
                previousRow.TestName = CleanCell($"{previousRow.TestName} {columns.Test}");
                previousRow.RawLineText = CleanCell($"{previousRow.RawLineText} {text}");
                continue;
            }

            var row = BuildRow(line, columns, rowIndex, ResolveLineOrdinal(line, lineOrdinals));
            if (row is null)
            {
                report.UnparsedLines.Add(new LabReportLineReference
                {
                    LineIndex = ResolveLineOrdinal(line, lineOrdinals),
                    Text = text,
                    Confidence = line.Confidence,
                });
                continue;
            }

            currentSection.Rows.Add(row);
            if (row.ParsingStatus != "parsed")
            {
                report.Warnings.Add($"Line {row.SourceLineIndex} for '{row.TestName}' is incomplete and should be reviewed.");
            }

            rowIndex++;
        }

        if (report.Sections.All(section => section.Rows.Count == 0))
        {
            report.Warnings.Add("No structured observation rows were extracted from the OCR.");
        }

        var rawJson = JsonSerializer.Serialize(report, JsonOptions);
        return new LabReportStructuringResult(true, report, rawJson, null);
    }

    private static Dictionary<LineReference, List<OcrWordBox>> BuildWordsByLine(OcrLayoutResult layout, List<OcrLineBox> lines)
    {
        if (layout.Words.Count == 0)
        {
            return [];
        }

        var hasLineMetadata = layout.Lines.Any(line => line.Page > 0 || line.Block > 0 || line.Paragraph > 0 || line.Line > 0)
            && layout.Words.Any(word => word.Page > 0 || word.Block > 0 || word.Paragraph > 0 || word.Line > 0);

        if (hasLineMetadata)
        {
            var keyedLines = lines
                .Where(line => line.Page > 0 || line.Block > 0 || line.Paragraph > 0 || line.Line > 0)
                .GroupBy(CreateLineKey)
                .ToDictionary(group => group.Key, group => group.First());

            return layout.Words
                .Where(word => word.Page > 0 || word.Block > 0 || word.Paragraph > 0 || word.Line > 0)
                .GroupBy(CreateLineKey)
                .Where(group => keyedLines.ContainsKey(group.Key))
                .ToDictionary(
                    group => new LineReference(keyedLines[group.Key]),
                    group => group.OrderBy(word => word.Left).ToList());
        }

        return BuildWordsByGeometry(layout.Words, lines);
    }

    private static LineKey CreateLineKey(OcrLineBox line) => new(line.Page, line.Block, line.Paragraph, line.Line);

    private static LineKey CreateLineKey(OcrWordBox word) => new(word.Page, word.Block, word.Paragraph, word.Line);

    private static Dictionary<LineReference, List<OcrWordBox>> BuildWordsByGeometry(List<OcrWordBox> words, List<OcrLineBox> lines)
    {
        var mapping = new Dictionary<LineReference, List<OcrWordBox>>();

        foreach (var line in lines)
        {
            var lineTop = line.Top - 6;
            var lineBottom = line.Top + line.Height + 6;
            var lineLeft = Math.Max(0, line.Left - 24);
            var lineRight = line.Left + line.Width + 24;

            var matchedWords = words
                .Where(word =>
                {
                    var wordCenterY = word.Top + (word.Height / 2.0);
                    var wordLeft = word.Left;
                    var wordRight = word.Left + word.Width;

                    return wordCenterY >= lineTop
                        && wordCenterY <= lineBottom
                        && wordRight >= lineLeft
                        && wordLeft <= lineRight;
                })
                .OrderBy(word => word.Left)
                .ToList();

            mapping[new LineReference(line)] = matchedWords;
        }

        return mapping;
    }

    private static ColumnAnchors DetectColumnAnchors(
        OcrLayoutResult layout,
        OcrLineBox? headerLine,
        IReadOnlyDictionary<LineReference, List<OcrWordBox>> wordsByLine)
    {
        if (headerLine is not null
            && wordsByLine.TryGetValue(new LineReference(headerLine), out var headerWords)
            && headerWords.Count > 0)
        {
            var resultStart = FindWordLeft(headerWords, "Result") ?? (int)(layout.ImageWidth * 0.38);
            var unitStart = FindWordLeft(headerWords, "Unit") ?? (int)(layout.ImageWidth * 0.56);
            var referenceStart = FindWordLeft(headerWords, "Biological")
                ?? FindWordLeft(headerWords, "Ref.")
                ?? (int)(layout.ImageWidth * 0.73);

            return new ColumnAnchors(resultStart, unitStart, referenceStart);
        }

        return new ColumnAnchors(
            (int)(layout.ImageWidth * 0.38),
            (int)(layout.ImageWidth * 0.56),
            (int)(layout.ImageWidth * 0.73));
    }

    private static int? FindWordLeft(IEnumerable<OcrWordBox> words, string target)
    {
        return words.FirstOrDefault(word => string.Equals(word.Text, target, StringComparison.OrdinalIgnoreCase))?.Left;
    }

    private static string? ExtractPatientDisplayName(List<OcrLineBox> lines, OcrLineBox? titleLine)
    {
        var cutoff = titleLine?.Top ?? 600;
        foreach (var line in lines.Where(line => line.Top < cutoff))
        {
            var text = CleanText(line.Text);
            if (string.IsNullOrWhiteSpace(text)
                || ContainsAny(text, "Diagnostic", "Laboratory", "Sample Id", "Report Release Time", "Dr.", "Time :"))
            {
                continue;
            }

            if (Regex.IsMatch(text, @"\b[A-Z][a-z]+\s+[A-Z]\.\s+[A-Z][a-z]+", RegexOptions.CultureInvariant))
            {
                return text;
            }
        }

        return null;
    }

    private static List<LabReportField> ExtractHeaderFields(
        List<OcrLineBox> lines,
        OcrLineBox? titleLine,
        IReadOnlyDictionary<OcrLineBox, int> lineOrdinals)
    {
        var fields = new List<LabReportField>();
        var cutoff = titleLine?.Top ?? 650;

        foreach (var line in lines.Where(line => line.Top < cutoff))
        {
            var text = CleanText(line.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var sampleIdMatch = SampleIdRegex.Match(text);
            if (sampleIdMatch.Success)
            {
                fields.Add(new LabReportField
                {
                    Label = "Sample Id",
                    Value = sampleIdMatch.Groups["value"].Value,
                    SourceLineIndex = ResolveLineOrdinal(line, lineOrdinals),
                    Confidence = line.Confidence,
                });
            }

            if (ContainsIgnoreCase(text, "Dr."))
            {
                fields.Add(new LabReportField
                {
                    Label = "Ordering Provider",
                    Value = CleanCell(text.Replace(":", string.Empty)),
                    SourceLineIndex = ResolveLineOrdinal(line, lineOrdinals),
                    Confidence = line.Confidence,
                });
            }

            if (ContainsIgnoreCase(text, "Hospital"))
            {
                fields.Add(new LabReportField
                {
                    Label = "Facility",
                    Value = text,
                    SourceLineIndex = ResolveLineOrdinal(line, lineOrdinals),
                    Confidence = line.Confidence,
                });
            }

            if (ContainsIgnoreCase(text, "Report Release Time"))
            {
                fields.Add(new LabReportField
                {
                    Label = "Report Release Time",
                    Value = TimeRegex.Match(text).Groups["value"].Value,
                    SourceLineIndex = ResolveLineOrdinal(line, lineOrdinals),
                    Confidence = line.Confidence,
                });
            }
        }

        return fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .GroupBy(field => (field.Label, field.Value))
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsSectionHeading(string text)
    {
        var trimmed = text.Trim().Trim(':');
        return trimmed switch
        {
            "DIFFERENTIAL COUNT" => true,
            "PLATELETS" => true,
            "ABSOLUTE COUNTS" => true,
            _ => false,
        };
    }

    private static string NormalizeHeading(string text) => CleanText(text).Trim().Trim(':');

    private static bool IsFooterLine(string text)
    {
        return text.StartsWith("Specimen", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Equipment", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Method", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Blood Peripheral", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("**CBC", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Note", StringComparison.OrdinalIgnoreCase)
            || ContainsIgnoreCase(text, "End OF Report")
            || ContainsIgnoreCase(text, "End Of Report");
    }

    private static bool ShouldAppendToPreviousRow(RowColumns columns, LabReportSection section)
    {
        return section.Rows.Count > 0
            && !string.IsNullOrWhiteSpace(columns.Test)
            && string.IsNullOrWhiteSpace(columns.Result)
            && string.IsNullOrWhiteSpace(columns.Unit)
            && string.IsNullOrWhiteSpace(columns.ReferenceRange);
    }

    private static RowColumns SplitIntoColumns(OcrLineBox line, List<OcrWordBox> words, ColumnAnchors anchors)
    {
        if (words.Count == 0)
        {
            return new RowColumns(CleanText(line.Text), string.Empty, string.Empty, string.Empty);
        }

        var testWords = new List<string>();
        var resultWords = new List<string>();
        var unitWords = new List<string>();
        var referenceWords = new List<string>();

        foreach (var word in words)
        {
            if (word.Left >= anchors.ReferenceStart)
            {
                referenceWords.Add(word.Text);
            }
            else if (word.Left >= anchors.UnitStart)
            {
                unitWords.Add(word.Text);
            }
            else if (word.Left >= anchors.ResultStart)
            {
                resultWords.Add(word.Text);
            }
            else
            {
                testWords.Add(word.Text);
            }
        }

        return new RowColumns(
            CleanCell(string.Join(" ", testWords)),
            CleanCell(string.Join(" ", resultWords)),
            CleanCell(string.Join(" ", unitWords)),
            CleanCell(string.Join(" ", referenceWords)));
    }

    private static LabReportRow? BuildRow(OcrLineBox line, RowColumns columns, int rowIndex, int sourceLineIndex)
    {
        var testName = CleanTestName(columns.Test);
        if (string.IsNullOrWhiteSpace(testName))
        {
            return null;
        }

        var resultText = CleanCell(columns.Result);
        var flag = FlagRegex.Match(resultText).Groups["flag"].Value;
        var numericValue = NumericValueRegex.Match(resultText).Value;

        if (string.IsNullOrWhiteSpace(numericValue) && string.IsNullOrWhiteSpace(resultText))
        {
            numericValue = string.Empty;
        }

        var row = new LabReportRow
        {
            RowIndex = rowIndex,
            SourceLineIndex = sourceLineIndex,
            TestName = testName,
            RawLineText = CleanText(line.Text),
            ResultText = resultText,
            NumericValueText = NormalizeNumericValue(numericValue),
            InterpretationFlag = string.IsNullOrWhiteSpace(flag) ? null : flag,
            UnitText = CleanCell(columns.Unit),
            NormalizedUnit = NormalizeUnit(columns.Unit),
            ReferenceRangeText = CleanCell(columns.ReferenceRange),
            Confidence = line.Confidence,
            ParsingStatus = DetermineParsingStatus(resultText, columns.Unit, columns.ReferenceRange),
        };

        return row;
    }

    private static string DetermineParsingStatus(string resultText, string unitText, string referenceRangeText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return "missing-result";
        }

        if (string.IsNullOrWhiteSpace(unitText) || string.IsNullOrWhiteSpace(referenceRangeText))
        {
            return "partial";
        }

        return "parsed";
    }

    private static string CleanTestName(string value)
    {
        var cleaned = CleanCell(value);
        cleaned = cleaned.TrimStart('@', '{', '#', '}', '(', ')');
        cleaned = cleaned.Replace(" :", string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Trim();
        return cleaned;
    }

    private static string CleanCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" .", ".", StringComparison.Ordinal)
            .Replace(" :", ":", StringComparison.Ordinal)
            .Replace(" ;", ";", StringComparison.Ordinal)
            .Trim();

        return Regex.Replace(cleaned, @"\s{2,}", " ");
    }

    private static string CleanText(string? value)
    {
        return CleanCell(value)
            .Replace('—', '-')
            .Replace('–', '-');
    }

    private static string? NormalizeNumericValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Replace(',', '.');
    }

    private static string? NormalizeUnit(string unitText)
    {
        var cleaned = CleanCell(unitText).ToLowerInvariant();
        return cleaned switch
        {
            "ful" or "fu" or "jul" or "dul" or "/ul" => "/uL",
            "pg" => "Pg",
            "fl" => "fL",
            "g/dl" => "g/dL",
            "gm/dl" => "gm/dL",
            _ => string.IsNullOrWhiteSpace(cleaned) ? null : unitText.Trim(),
        };
    }

    private static bool ContainsIgnoreCase(string text, string value)
    {
        return text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => ContainsIgnoreCase(text, value));
    }

    private static int ResolveLineOrdinal(OcrLineBox line, IReadOnlyDictionary<OcrLineBox, int> lineOrdinals)
    {
        if (line.LineIndex > 0)
        {
            return line.LineIndex;
        }

        return lineOrdinals.TryGetValue(line, out var ordinal) ? ordinal : 0;
    }

    private sealed record LineKey(int Page, int Block, int Paragraph, int Line);

    private sealed record LineReference(int Top, int Left, int Width, int Height)
    {
        public LineReference(OcrLineBox line)
            : this(line.Top, line.Left, line.Width, line.Height)
        {
        }
    }

    private sealed record ColumnAnchors(int ResultStart, int UnitStart, int ReferenceStart);

    private sealed record RowColumns(string Test, string Result, string Unit, string ReferenceRange);
}
