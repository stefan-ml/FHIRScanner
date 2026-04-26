# FHIRScanner

FHIRScanner is a Blazor Server app for inspecting OCR output from lab report images, structuring detected rows, and generating draft FHIR resources. The current pipeline is:

```text
image upload -> Tesseract OCR with coordinates -> structured lab report JSON -> Ollama/OpenAI FHIR draft -> local fallback draft
```

## 1. Install Required Software

Run PowerShell as a normal user and install the main dependencies:

```powershell
winget install Microsoft.DotNet.SDK.9
winget install Python.Python.3.12
winget install UB-Mannheim.TesseractOCR
winget install Ollama.Ollama
```

Close and reopen PowerShell after installing so `PATH` updates are loaded.

Verify the tools:

```powershell
dotnet --version
python --version
python -m pip --version
& "C:\Program Files\Tesseract-OCR\tesseract.exe" --version
ollama --version
```

If `python --version` prints this error:

```text
Python was not found; run without arguments to install from the Microsoft Store...
```

install Python from `winget` or from `https://www.python.org/downloads/windows/`, enable `Add python.exe to PATH`, then reopen PowerShell. If Windows still opens the Microsoft Store alias, disable these aliases:

```text
Settings -> Apps -> Advanced app settings -> App execution aliases
```

Turn off:

```text
python.exe
python3.exe
```

Then verify again:

```powershell
python --version
```

## 2. Install Python Packages

From the repository root:

```powershell
cd C:\projects\FHIRScanner-main\FHIRScanner-main
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
```

The OCR script uses:

```text
pytesseract
Pillow
```

## 3. Start Ollama And Install The Model

Pull the local model used by development config:

```powershell
ollama pull qwen2.5vl:7b
ollama list
```

Make sure the Ollama server is running. In one PowerShell window:

```powershell
ollama serve
```

In another PowerShell window, verify the API:

```powershell
Invoke-RestMethod http://localhost:11434/api/tags
```

Development config is in:

```text
FHIRScanner\appsettings.Development.json
```

It should contain:

```json
"OpenAI": {
  "Provider": "Ollama",
  "BaseUrl": "http://localhost:11434/",
  "Model": "qwen2.5vl:7b"
}
```

If `ollama list` shows a different model name, copy that exact name into `Model`.

## 4. Run The App

From the repository root:

```powershell
dotnet run --project .\FHIRScanner\FHIRScanner.csproj
```

Open the URL printed by `dotnet run`, upload an image, click `Run OCR`, then click `Generate FHIR Draft`.

Runtime folders are created automatically:

```text
FHIRScanner\Storage\Uploads
FHIRScanner\Storage\OcrResults
FHIRScanner\Storage\FhirDrafts
```

