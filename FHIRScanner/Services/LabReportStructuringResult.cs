using System.Text.Json;

namespace FHIRScanner.Services;

public sealed record LabReportStructuringResult(
    bool Succeeded,
    StructuredLabReport? Report,
    string RawJson,
    string? ErrorMessage);

public sealed class StructuredLabReport
{
    public string Source { get; set; } = string.Empty;

    public string DocumentType { get; set; } = "lab-report";

    public string? ReportTitle { get; set; }

    public string? PatientDisplayName { get; set; }

    public List<LabReportField> HeaderFields { get; set; } = [];

    public List<LabReportSection> Sections { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<LabReportLineReference> UnparsedLines { get; set; } = [];
}

public sealed class LabReportField
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public int SourceLineIndex { get; set; }

    public double Confidence { get; set; }
}

public sealed class LabReportSection
{
    public string Name { get; set; } = string.Empty;

    public List<LabReportRow> Rows { get; set; } = [];
}

public sealed class LabReportRow
{
    public int RowIndex { get; set; }

    public int SourceLineIndex { get; set; }

    public string TestName { get; set; } = string.Empty;

    public string RawLineText { get; set; } = string.Empty;

    public string ResultText { get; set; } = string.Empty;

    public string? NumericValueText { get; set; }

    public string? InterpretationFlag { get; set; }

    public string UnitText { get; set; } = string.Empty;

    public string? NormalizedUnit { get; set; }

    public string ReferenceRangeText { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string ParsingStatus { get; set; } = "parsed";
}

public sealed class LabReportLineReference
{
    public int LineIndex { get; set; }

    public string Text { get; set; } = string.Empty;

    public double Confidence { get; set; }
}

public sealed class StructuredReportForPrompt
{
    public string Source { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public string? ReportTitle { get; set; }

    public string? PatientDisplayName { get; set; }

    public List<LabReportField> HeaderFields { get; set; } = [];

    public List<LabReportSection> Sections { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}
