using System.Text.Json;

namespace FHIRScanner.Services;

public sealed record FhirDraftResult(
    bool Succeeded,
    string Status,
    string? OutputRelativePath,
    string? RawJson,
    FhirDraftPayload? Payload,
    string? ErrorMessage);

public sealed class FhirDraftPayload
{
    public string Source { get; init; } = string.Empty;

    public List<FhirDraftResource> Resources { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public List<string> MissingFields { get; init; } = [];
}

public sealed class FhirDraftResource
{
    public string ResourceType { get; init; } = string.Empty;

    public string Rationale { get; init; } = string.Empty;

    public JsonElement Resource { get; init; }
}
