using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FHIRScanner.Services;

public sealed class OcrProcessingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> SupportedImageExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff",
        ".bmp",
        ".webp",
    ];

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<OcrProcessingService> _logger;
    private readonly OcrProcessingOptions _options;

    public OcrProcessingService(
        IWebHostEnvironment environment,
        ILogger<OcrProcessingService> logger,
        IOptions<OcrProcessingOptions> options)
    {
        _environment = environment;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<OcrProcessingResult> ProcessAsync(string storedFileName, CancellationToken cancellationToken = default)
    {
        var uploadPath = Path.Combine(_environment.ContentRootPath, "Storage", "Uploads", storedFileName);
        if (!File.Exists(uploadPath))
        {
            return new OcrProcessingResult(
                false,
                "Missing file",
                null,
                null,
                null,
                $"The uploaded file '{storedFileName}' could not be found.");
        }

        var extension = Path.GetExtension(storedFileName);
        if (!SupportedImageExtensions.Contains(extension))
        {
            return new OcrProcessingResult(
                false,
                "Unsupported file",
                null,
                null,
                null,
                $"Only image OCR is enabled right now. '{extension}' is not supported in this OCR-only mode.");
        }

        var script = ResolveScript();
        if (script is null)
        {
            return new OcrProcessingResult(
                false,
                "OCR unavailable",
                null,
                null,
                null,
                $"OCR script '{_options.ScriptRelativePath}' was not found under the app folder or its parent folders.");
        }

        var outputDirectory = Path.Combine(_environment.ContentRootPath, _options.OutputRelativePath);
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{Path.GetFileNameWithoutExtension(storedFileName)}.ocr.json";
        var outputPath = Path.Combine(outputDirectory, outputFileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            WorkingDirectory = script.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(script.Path);
        startInfo.ArgumentList.Add(uploadPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add(_options.Language);
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add(_options.PageSegmentationMode.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--min-confidence");
        startInfo.ArgumentList.Add(_options.MinimumConfidence.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdOutTask;
            var stderr = await stdErrTask;

            if (process.ExitCode != 0)
            {
                var errorMessage = BuildErrorMessage(stderr, stdout);
                _logger.LogWarning(
                    "OCR process failed for {StoredFileName} with exit code {ExitCode}: {ErrorMessage}",
                    storedFileName,
                    process.ExitCode,
                    errorMessage);

                return new OcrProcessingResult(false, "OCR failed", null, null, null, errorMessage);
            }

            if (!File.Exists(outputPath))
            {
                return new OcrProcessingResult(
                    false,
                    "Missing output",
                    null,
                    null,
                    null,
                    "OCR finished without producing a layout result file.");
            }

            var rawJson = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var layout = JsonSerializer.Deserialize<OcrLayoutResult>(rawJson, JsonOptions);
            if (layout is null)
            {
                return new OcrProcessingResult(
                    false,
                    "Invalid output",
                    Path.Combine(_options.OutputRelativePath, outputFileName),
                    rawJson,
                    null,
                    "OCR output could not be parsed.");
            }

            return new OcrProcessingResult(
                true,
                "OCR ready",
                Path.Combine(_options.OutputRelativePath, outputFileName),
                rawJson,
                layout,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected OCR failure for {StoredFileName}", storedFileName);
            return new OcrProcessingResult(false, "OCR failed", null, null, null, ex.Message);
        }
    }

    private ResolvedScript? ResolveScript()
    {
        var current = new DirectoryInfo(_environment.ContentRootPath);
        while (current is not null)
        {
            var scriptPath = Path.GetFullPath(Path.Combine(current.FullName, _options.ScriptRelativePath));
            if (File.Exists(scriptPath))
            {
                return new ResolvedScript(scriptPath, current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    private static string BuildErrorMessage(string stderr, string stdout)
    {
        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(details))
        {
            return "The OCR process exited with an unknown error.";
        }

        return details.Trim();
    }

    private sealed record ResolvedScript(string Path, string WorkingDirectory);
}
