namespace FHIRScanner.Services;

public sealed record OcrProcessingResult(
    bool Succeeded,
    string Status,
    string? OutputRelativePath,
    string? RawJson,
    OcrLayoutResult? Layout,
    string? ErrorMessage);

public sealed class OcrLayoutResult
{
    public string Source { get; init; } = string.Empty;

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public string FullText { get; init; } = string.Empty;

    public double AverageConfidence { get; init; }

    public int LineCount { get; init; }

    public int WordCount { get; init; }

    public List<OcrLineBox> Lines { get; init; } = [];

    public List<OcrWordBox> Words { get; init; } = [];
}

public sealed class OcrLineBox
{
    public int LineIndex { get; init; }

    public int Page { get; init; }

    public int Block { get; init; }

    public int Paragraph { get; init; }

    public int Line { get; init; }

    public string Text { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public int Left { get; init; }

    public int Top { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed class OcrWordBox
{
    public int WordIndex { get; init; }

    public int Page { get; init; }

    public int Block { get; init; }

    public int Paragraph { get; init; }

    public int Line { get; init; }

    public string Text { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public int Left { get; init; }

    public int Top { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}
