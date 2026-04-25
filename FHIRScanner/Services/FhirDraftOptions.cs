namespace FHIRScanner.Services;

public sealed class FhirDraftOptions
{
    public string OutputRelativePath { get; set; } = Path.Combine("Storage", "FhirDrafts");

    public int MaxLinesToSend { get; set; } = 40;

    public int MaxLineTextLength { get; set; } = 140;

    public int MaxFullTextChars { get; set; } = 2500;

    public int RetryMaxLinesToSend { get; set; } = 18;

    public int RetryMaxLineTextLength { get; set; } = 90;

    public int RetryMaxFullTextChars { get; set; } = 900;

    public int EmergencyMaxLinesToSend { get; set; } = 10;

    public int EmergencyMaxLineTextLength { get; set; } = 70;

    public int EmergencyMaxFullTextChars { get; set; } = 450;
}
