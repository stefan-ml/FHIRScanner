using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FHIRScanner.Services;

public sealed class FhirDraftingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FhirDraftingService> _logger;
    private readonly OpenAiOptions _openAiOptions;
    private readonly FhirDraftOptions _fhirDraftOptions;

    public FhirDraftingService(
        HttpClient httpClient,
        IWebHostEnvironment environment,
        ILogger<FhirDraftingService> logger,
        IOptions<OpenAiOptions> openAiOptions,
        IOptions<FhirDraftOptions> fhirDraftOptions)
    {
        _httpClient = httpClient;
        _environment = environment;
        _logger = logger;
        _openAiOptions = openAiOptions.Value;
        _fhirDraftOptions = fhirDraftOptions.Value;
    }

    public async Task<FhirDraftResult> GenerateAsync(
        string storedFileName,
        OcrLayoutResult? layout,
        CancellationToken cancellationToken = default)
    {
        if (layout is null)
        {
            return new FhirDraftResult(
                false,
                "FHIR input missing",
                null,
                null,
                null,
                "Run OCR first so the model has structured text and coordinates to work from.");
        }

        var apiKey = ResolveApiKey();
        var canSkipApiKey = CanSkipApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) && !canSkipApiKey)
        {
            return new FhirDraftResult(
                false,
                "LLM not configured",
                null,
                null,
                null,
                "Set OPENAI_API_KEY or OpenAI:ApiKey before generating FHIR drafts, or point BaseUrl to a local LM Studio server.");
        }

        try
        {
            var budgets = new[]
            {
                new PromptBudget(
                    _fhirDraftOptions.MaxLinesToSend,
                    _fhirDraftOptions.MaxLineTextLength,
                    _fhirDraftOptions.MaxFullTextChars,
                    "primary"),
                new PromptBudget(
                    _fhirDraftOptions.RetryMaxLinesToSend,
                    _fhirDraftOptions.RetryMaxLineTextLength,
                    _fhirDraftOptions.RetryMaxFullTextChars,
                    "retry"),
                new PromptBudget(
                    _fhirDraftOptions.EmergencyMaxLinesToSend,
                    _fhirDraftOptions.EmergencyMaxLineTextLength,
                    _fhirDraftOptions.EmergencyMaxFullTextChars,
                    "emergency"),
            };

            string? lastError = null;

            foreach (var budget in budgets)
            {
                var attempt = await SendDraftRequestAsync(storedFileName, layout, apiKey, budget, cancellationToken);
                if (attempt.Succeeded)
                {
                    return attempt.Result!;
                }

                lastError = attempt.ErrorMessage;
                if (!attempt.ShouldRetry)
                {
                    break;
                }
            }

            return new FhirDraftResult(
                false,
                "FHIR draft failed",
                null,
                null,
                null,
                lastError ?? "FHIR draft generation failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FHIR draft generation failed for {StoredFileName}", storedFileName);
            return new FhirDraftResult(false, "FHIR draft failed", null, null, null, ex.Message);
        }
    }

    private string ResolveApiKey()
    {
        return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _openAiOptions.ApiKey;
    }

    private bool CanSkipApiKey()
    {
        if (!_openAiOptions.AllowMissingApiKeyForLocalhost)
        {
            return false;
        }

        if (!Uri.TryCreate(_openAiOptions.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool Succeeded, bool ShouldRetry, FhirDraftResult? Result, string? ErrorMessage)> SendDraftRequestAsync(
        string storedFileName,
        OcrLayoutResult layout,
        string? apiKey,
        PromptBudget budget,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(layout, budget);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (!string.IsNullOrWhiteSpace(_openAiOptions.Organization))
        {
            request.Headers.Add("OpenAI-Organization", _openAiOptions.Organization);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var formattedError = $"OpenAI request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}";
            var shouldRetry = IsContextLengthError(responseBody);
            _logger.LogWarning(
                "FHIR draft request failed on {BudgetLabel} budget with status {StatusCode}: {Body}",
                budget.Label,
                response.StatusCode,
                responseBody);

            return (false, shouldRetry, null, formattedError);
        }

        var outputJson = ExtractOutputJson(responseBody);
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return (false, false, null, "The OpenAI response did not contain JSON output.");
        }

        FhirDraftPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FhirDraftPayload>(outputJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            var shouldRetry = LooksLikeMalformedJson(outputJson);
            _logger.LogWarning(ex, "FHIR draft response was not valid JSON on {BudgetLabel} budget.", budget.Label);
            return (false, shouldRetry, null, $"The model returned invalid JSON. {ex.Message}");
        }

        if (payload is null)
        {
            return (false, true, null, "The OpenAI response JSON could not be parsed into the expected FHIR draft shape.");
        }

        var outputDirectory = Path.Combine(_environment.ContentRootPath, _fhirDraftOptions.OutputRelativePath);
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{Path.GetFileNameWithoutExtension(storedFileName)}.fhir-draft.json";
        var outputPath = Path.Combine(outputDirectory, outputFileName);
        await File.WriteAllTextAsync(outputPath, outputJson, cancellationToken);

        return (
            true,
            false,
            new FhirDraftResult(
                true,
                "FHIR draft ready",
                Path.Combine(_fhirDraftOptions.OutputRelativePath, outputFileName),
                outputJson,
                payload,
                null),
            null);
    }

    private string BuildRequestBody(OcrLayoutResult layout, PromptBudget budget)
    {
        var systemPrompt = """
            Convert OCR text into draft FHIR resources.
            Use only facts present in OCR.
            Do not invent missing values.
            Use real FHIR resource types only.
            Return at most 3 resources.
            No markdown fences.
            Return valid JSON:
            {
              "source": "filename",
              "resources": [
                {
                  "resourceType": "FHIR type",
                  "rationale": "short reason",
                  "resource": {}
                }
              ],
              "warnings": [],
              "missingFields": []
            }
            """;

        var compactOcrJson = BuildCompactOcrJson(layout, budget);
        var userPrompt = $"""
            Generate a draft FHIR mapping from this OCR result.

            OCR data:
            {compactOcrJson}
            """;

        var payload = new
        {
            model = _openAiOptions.Model,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            text = new
            {
                format = new
                {
                    type = "json_object",
                }
            },
            max_output_tokens = 700,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private string BuildCompactOcrJson(OcrLayoutResult layout, PromptBudget budget)
    {
        var selectedLines = layout.Lines
            .Take(budget.MaxLinesToSend)
            .Select(line => new
            {
                lineIndex = line.LineIndex,
                text = Truncate(line.Text, budget.MaxLineTextLength),
                confidence = Math.Round(line.Confidence, 2),
                left = line.Left,
                top = line.Top,
                width = line.Width,
                height = line.Height,
            })
            .ToArray();

        var compactPayload = new
        {
            source = layout.Source,
            imageWidth = layout.ImageWidth,
            imageHeight = layout.ImageHeight,
            lineCount = layout.LineCount,
            wordCount = layout.WordCount,
            averageConfidence = Math.Round(layout.AverageConfidence, 2),
            fullTextPreview = Truncate(layout.FullText, budget.MaxFullTextChars),
            includedLineCount = selectedLines.Length,
            totalLineCount = layout.Lines.Count,
            truncated = layout.Lines.Count > selectedLines.Length || layout.FullText.Length > budget.MaxFullTextChars,
            lines = selectedLines,
        };

        return JsonSerializer.Serialize(compactPayload, JsonOptions);
    }

    private static bool IsContextLengthError(string responseBody)
    {
        return responseBody.Contains("context length", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("n_keep", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("n_ctx", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }

    private static string? ExtractOutputJson(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return SanitizeJsonCandidate(outputText.GetString());
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var outputItem in outputArray.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentArray.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    return SanitizeJsonCandidate(textElement.GetString());
                }
            }
        }

        return null;
    }

    private static string? SanitizeJsonCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        var trimmed = candidate.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed.Trim('`').Trim();
        }

        var withoutFenceHeader = trimmed[(firstNewline + 1)..];
        var closingFenceIndex = withoutFenceHeader.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex >= 0)
        {
            withoutFenceHeader = withoutFenceHeader[..closingFenceIndex];
        }

        return withoutFenceHeader.Trim();
    }

    private static bool LooksLikeMalformedJson(string candidate)
    {
        var trimmed = candidate.Trim();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal)
            || trimmed.StartsWith("```", StringComparison.Ordinal);
    }
}

internal sealed record PromptBudget(int MaxLinesToSend, int MaxLineTextLength, int MaxFullTextChars, string Label);
