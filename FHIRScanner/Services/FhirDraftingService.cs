using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FHIRScanner.Services;

public sealed class FhirDraftingService
{
    private static readonly Regex ReferenceRangeRegex = new(@"(?<low>\d+(?:[.,]\d+)?)\s*-\s*(?<high>\d+(?:[.,]\d+)?)", RegexOptions.Compiled);

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
        StructuredLabReport? report,
        CancellationToken cancellationToken = default)
    {
        if (report is null)
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
                var attempt = await SendDraftRequestAsync(storedFileName, report, apiKey, budget, cancellationToken);
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

            return await BuildLocalDraftAsync(storedFileName, report, lastError, cancellationToken);
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
        StructuredLabReport report,
        string? apiKey,
        PromptBudget budget,
        CancellationToken cancellationToken)
    {
        var useOllama = IsOllamaProvider();
        var requestBody = useOllama
            ? BuildOllamaChatRequestBody(report, budget)
            : BuildOpenAiResponsesRequestBody(report, budget);
        var endpoint = useOllama ? "api/chat" : "responses";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        if (!useOllama && !string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (!useOllama && !string.IsNullOrWhiteSpace(_openAiOptions.Organization))
        {
            request.Headers.Add("OpenAI-Organization", _openAiOptions.Organization);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var providerName = useOllama ? "Ollama" : "OpenAI";
            var formattedError = $"{providerName} request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}";
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
            return (false, false, null, "The model response did not contain JSON output.");
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

    private string BuildOpenAiResponsesRequestBody(StructuredLabReport report, PromptBudget budget)
    {
        var (systemPrompt, userPrompt) = BuildPromptMessages(report, budget);

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
            max_output_tokens = budget.MaxOutputTokens,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private string BuildOllamaChatRequestBody(StructuredLabReport report, PromptBudget budget)
    {
        var (systemPrompt, userPrompt) = BuildPromptMessages(report, budget);
        var payload = new
        {
            model = _openAiOptions.Model,
            stream = false,
            format = "json",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            options = new
            {
                temperature = 0.1,
                num_predict = budget.MaxOutputTokens,
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private (string SystemPrompt, string UserPrompt) BuildPromptMessages(StructuredLabReport report, PromptBudget budget)
    {
        var systemPrompt = """
            Convert a structured lab report into draft FHIR resources.
            Use only facts present in the structured report.
            Do not invent missing values.
            Use real FHIR resource types only.
            Return one DiagnosticReport plus Observation resources only.
            Do not nest Observation objects inside DiagnosticReport.
            DiagnosticReport.result should contain references only.
            Keep each resource compact.
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

        var structuredReportJson = BuildStructuredReportJson(report, budget);
        var userPrompt = $"""
            Generate a draft FHIR mapping from this structured lab report.

            Structured report data:
            {structuredReportJson}
            """;

        return (systemPrompt, userPrompt);
    }

    private string BuildStructuredReportJson(StructuredLabReport report, PromptBudget budget)
    {
        var remainingRows = budget.MaxLinesToSend;
        var selectedSections = new List<LabReportSection>();

        foreach (var section in report.Sections)
        {
            if (remainingRows <= 0)
            {
                break;
            }

            var rows = section.Rows
                .Take(remainingRows)
                .Select(row => new LabReportRow
                {
                    RowIndex = row.RowIndex,
                    SourceLineIndex = row.SourceLineIndex,
                    TestName = row.TestName,
                    RawLineText = Truncate(row.RawLineText, budget.MaxLineTextLength),
                    ResultText = row.ResultText,
                    NumericValueText = row.NumericValueText,
                    InterpretationFlag = row.InterpretationFlag,
                    UnitText = row.UnitText,
                    NormalizedUnit = row.NormalizedUnit,
                    ReferenceRangeText = row.ReferenceRangeText,
                    Confidence = row.Confidence,
                    ParsingStatus = row.ParsingStatus,
                })
                .ToList();

            if (rows.Count == 0)
            {
                continue;
            }

            selectedSections.Add(new LabReportSection
            {
                Name = section.Name,
                Rows = rows,
            });

            remainingRows -= rows.Count;
        }

        var compactPayload = new StructuredReportForPrompt
        {
            Source = report.Source,
            DocumentType = report.DocumentType,
            ReportTitle = report.ReportTitle,
            PatientDisplayName = report.PatientDisplayName,
            HeaderFields = report.HeaderFields,
            Sections = selectedSections,
            Notes = report.Notes
                .Select(note => Truncate(note, budget.MaxLineTextLength))
                .ToList(),
            Warnings = report.Warnings,
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

        if (root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.Object
            && messageElement.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
        {
            return SanitizeJsonCandidate(messageContent.GetString());
        }

        if (root.TryGetProperty("response", out var ollamaResponse) && ollamaResponse.ValueKind == JsonValueKind.String)
        {
            return SanitizeJsonCandidate(ollamaResponse.GetString());
        }

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

    private bool IsOllamaProvider()
    {
        if (string.Equals(_openAiOptions.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(_openAiOptions.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Port == 11434;
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

    private async Task<FhirDraftResult> BuildLocalDraftAsync(
        string storedFileName,
        StructuredLabReport report,
        string? modelError,
        CancellationToken cancellationToken)
    {
        var payloadObject = BuildLocalPayloadObject(report, modelError);
        var outputJson = JsonSerializer.Serialize(payloadObject, JsonOptions);
        var payload = JsonSerializer.Deserialize<FhirDraftPayload>(outputJson, JsonOptions);

        if (payload is null)
        {
            return new FhirDraftResult(
                false,
                "FHIR draft failed",
                null,
                null,
                null,
                modelError ?? "Local FHIR draft fallback could not be parsed.");
        }

        var outputDirectory = Path.Combine(_environment.ContentRootPath, _fhirDraftOptions.OutputRelativePath);
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{Path.GetFileNameWithoutExtension(storedFileName)}.fhir-draft.json";
        var outputPath = Path.Combine(outputDirectory, outputFileName);
        await File.WriteAllTextAsync(outputPath, outputJson, cancellationToken);

        return new FhirDraftResult(
            true,
            "FHIR draft ready (local fallback)",
            Path.Combine(_fhirDraftOptions.OutputRelativePath, outputFileName),
            outputJson,
            payload,
            null);
    }

    private static object BuildLocalPayloadObject(StructuredLabReport report, string? modelError)
    {
        var rows = report.Sections
            .SelectMany(section => section.Rows.Select(row => new { Section = section.Name, Row = row }))
            .Where(item => !string.Equals(item.Row.ParsingStatus, "missing-result", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var observationResources = rows
            .Select((item, index) => BuildObservationResource(item.Row, item.Section, index + 1))
            .ToList();

        var resources = new List<object>();

        if (!string.IsNullOrWhiteSpace(report.PatientDisplayName))
        {
            resources.Add(new
            {
                resourceType = "Patient",
                rationale = "Patient name was extracted from the structured report header.",
                resource = new
                {
                    resourceType = "Patient",
                    id = Slug(report.PatientDisplayName),
                    name = new[]
                    {
                        new
                        {
                            text = CleanDisplayName(report.PatientDisplayName),
                        },
                    },
                },
            });
        }

        resources.Add(new
        {
            resourceType = "DiagnosticReport",
            rationale = "Report-level resource built from the structured lab report.",
            resource = new
            {
                resourceType = "DiagnosticReport",
                status = "final",
                code = new
                {
                    text = report.ReportTitle ?? "Lab report",
                },
                identifier = BuildIdentifier(report),
                subject = string.IsNullOrWhiteSpace(report.PatientDisplayName)
                    ? null
                    : new
                    {
                        reference = $"Patient/{Slug(report.PatientDisplayName)}",
                        display = CleanDisplayName(report.PatientDisplayName),
                    },
                result = observationResources
                    .Select(resource => new
                    {
                        reference = $"Observation/{resource.Id}",
                        display = resource.Display,
                    })
                    .ToArray(),
            },
        });

        resources.AddRange(observationResources.Select(resource => resource.Resource));

        var warnings = report.Warnings.ToList();
        warnings.Add("FHIR draft was generated locally because the model returned malformed or truncated JSON.");
        if (!string.IsNullOrWhiteSpace(modelError))
        {
            warnings.Add(Truncate(modelError, 220));
        }

        return new
        {
            source = report.Source,
            resources,
            warnings,
            missingFields = Array.Empty<string>(),
        };
    }

    private static (string Id, string Display, object Resource) BuildObservationResource(LabReportRow row, string sectionName, int index)
    {
        var id = $"obs-{index:000}-{Slug(row.TestName)}";
        var value = ParseDecimal(row.NumericValueText);
        var range = ParseReferenceRange(row.ReferenceRangeText);

        var resource = new
        {
            resourceType = "Observation",
            rationale = $"Observation built from structured row {row.RowIndex} in {sectionName}.",
            resource = new
            {
                resourceType = "Observation",
                id,
                status = "final",
                category = new[]
                {
                    new
                    {
                        text = sectionName,
                    },
                },
                code = new
                {
                    text = row.TestName,
                },
                valueQuantity = value is null
                    ? null
                    : new
                    {
                        value,
                        unit = row.NormalizedUnit ?? row.UnitText,
                    },
                interpretation = string.IsNullOrWhiteSpace(row.InterpretationFlag)
                    ? null
                    : new[]
                    {
                        new
                        {
                            text = row.InterpretationFlag,
                        },
                    },
                referenceRange = range is null
                    ? Array.Empty<object>()
                    : new[]
                    {
                        new
                        {
                            low = new
                            {
                                value = range.Value.Low,
                                unit = row.NormalizedUnit ?? row.UnitText,
                            },
                            high = new
                            {
                                value = range.Value.High,
                                unit = row.NormalizedUnit ?? row.UnitText,
                            },
                            text = row.ReferenceRangeText,
                        },
                    },
                note = new[]
                {
                    new
                    {
                        text = $"OCR confidence {row.Confidence:0.##}; source line {row.SourceLineIndex}; raw: {row.RawLineText}",
                    },
                },
            },
        };

        return (id, row.TestName, resource);
    }

    private static object[] BuildIdentifier(StructuredLabReport report)
    {
        return report.HeaderFields
            .Where(field => string.Equals(field.Label, "Sample Id", StringComparison.OrdinalIgnoreCase))
            .Select(field => new
            {
                value = field.Value,
            })
            .Cast<object>()
            .ToArray();
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(',', '.');
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static (decimal Low, decimal High)? ParseReferenceRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ReferenceRangeRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var low = ParseDecimal(match.Groups["low"].Value);
        var high = ParseDecimal(match.Groups["high"].Value);
        return low is null || high is null ? null : (low.Value, high.Value);
    }

    private static string CleanDisplayName(string value)
    {
        return value
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string Slug(string value)
    {
        var cleaned = Regex.Replace(CleanDisplayName(value).ToLowerInvariant(), @"[^a-z0-9]+", "-");
        return cleaned.Trim('-');
    }
}

internal sealed record PromptBudget(int MaxLinesToSend, int MaxLineTextLength, int MaxFullTextChars, string Label)
{
    public int MaxOutputTokens => Label switch
    {
        "primary" => 1000,
        "retry" => 850,
        _ => 650,
    };
}
