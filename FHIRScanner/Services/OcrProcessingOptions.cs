namespace FHIRScanner.Services;

public sealed class OcrProcessingOptions
{
    public string PythonExecutable { get; set; } = "python";

    public string ScriptRelativePath { get; set; } = Path.Combine("ocr_to_fhir", "ocr_layout.py");

    public string OutputRelativePath { get; set; } = Path.Combine("Storage", "OcrResults");

    public string Language { get; set; } = "eng";

    public int PageSegmentationMode { get; set; } = 6;

    public int MinimumConfidence { get; set; } = 35;
}
