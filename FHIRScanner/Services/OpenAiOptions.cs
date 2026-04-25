namespace FHIRScanner.Services;

public sealed class OpenAiOptions
{
    public string Provider { get; set; } = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

    public string Model { get; set; } = "gpt-5.4";

    public string? Organization { get; set; }

    public bool AllowMissingApiKeyForLocalhost { get; set; } = true;
}
