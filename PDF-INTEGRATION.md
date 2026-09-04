# PDF Form + Redaction + PDF/A — integration notes

Native document tools for LmKitOmni: **PDF form read/fill**, **PDF redaction**,
**Office (OpenXML) redaction**, and **PDF/A validation**. These are *pure* LM-Kit.NET
document APIs — **no model, no network, no container** — so they are fully
CI-testable and run for real in the test host.

**Disabled by default** (`DocumentTools:Enabled = false`). When off, every endpoint
returns **501** and every service method throws `DocumentToolsDisabledException`.

The controller (`/api/documents`) and the two services are **self-contained and fully
tested without any agent tool-graph wiring**. This file contains the two snippets that
the coordinator must apply to the DO-NOT-EDIT files (`Program.cs`, `appsettings.json`),
plus a *recommendation* for wiring these into the agent tool-graph.

> **No DO-NOT-EDIT file was modified by this change.** The services are registered in
> the **test** host (see `DocumentsApiFactoryBase` in `LmKitOmniApi.Tests`), so the
> controller + services build and pass tests with `Program.cs` untouched. Apply the
> snippets below to enable them in the running app.

---

## Files added (all new; nothing existing was edited)

| File | Purpose |
| --- | --- |
| `LmKitOmniApi/Infrastructure/AI/Documents/DocumentToolsOptions.cs` | Options (`SectionName="DocumentTools"`; `Enabled=false`, `MaxInputBytes`, `MaxSearchTerms`, `MaxOutputBytes`). |
| `LmKitOmniApi/Infrastructure/AI/Documents/DocumentToolsExceptions.cs` | `DocumentToolsDisabledException` (→501), `DocumentValidationException` (→400). |
| `LmKitOmniApi/Infrastructure/AI/Documents/DocumentInputValidator.cs` | Model-free guards: enable gate, size caps, `%PDF` / `PK\x03\x04` magic-byte sniffing, term-count cap. Runs **before** any LM-Kit call. |
| `LmKitOmniApi/Infrastructure/AI/Documents/IPdfFormService.cs` + `PdfFormService.cs` | `GetFields(byte[])` → snapshot DTO; `Fill(byte[], values, flatten)` → `(byte[] Data, report)`. Wraps `LMKit.Document.Pdf.PdfForm`. |
| `LmKitOmniApi/Infrastructure/AI/Documents/IDocumentRedactionService.cs` + `DocumentRedactionService.cs` | `RedactPdf`, `RedactOffice`, `ValidatePdfA`. Wraps `PdfRedactor` / `OfficeRedactor` / `PdfAValidator`. |
| `LmKitOmniApi/Controllers/DocumentsController.cs` | `api/documents` — 5 owner-scoped endpoints (below). |
| `LmKitOmniApi.Tests/NativeDocumentEngine.cs`, `DocumentFixtures.cs`, `DocumentRedactionServiceTests.cs`, `PdfFormServiceTests.cs`, `PdfAValidatorTests.cs`, `DocumentsControllerTests.cs` | Tests. |

### Endpoints (all `[Authorize]`, owner-scoped, `MaxInputBytes` enforced on upload)

| Verb + route | Form fields | Returns |
| --- | --- | --- |
| `POST /api/documents/pdf/form/fields` | `file` | `{ hasForm, fields:[{name,label,kind,value,options,isRequired,isReadOnly,pageIndex}] }` |
| `POST /api/documents/pdf/form/fill` | `file`, `values` (JSON array of `{name,value}`), `flatten` (bool) | `{ fileId, name, report:{fieldsSet,fieldsSkipped,flattened,issues} }` |
| `POST /api/documents/pdf/redact` | `file`, `terms` (JSON array of strings, or newline-separated), `caseSensitive`, `wholeWord` | `{ fileId, name, report:{contentRemoved,searchMatches,removedGlyphs,removedTextObjects,removedImages,pagesProcessed} }` |
| `POST /api/documents/office/redact` | `file`, `terms`, `caseSensitive`, `wholeWord` | `{ fileId, name, report:{contentRemoved,partsScanned,replacedOccurrences} }` |
| `POST /api/documents/pdf-a/validate` | `file`, `level` (optional: `PdfA1b`\|`PdfA2b`\|`PdfA3b`) | `{ verdict, level, declaredConformance, pageCount, rulesEvaluated, findings:[{rule,description}] }` |

`fileId` is a server-generated name in the caller's isolated upload root; the produced
file is downloaded through the existing `GET /api/files/{id}`. A client-supplied path
is never accepted.

---

## 1. `Program.cs` — DI registration (apply this)

Insert immediately **after** the WebRead block (the three
`builder.Services...WebReadOptions / IWebPageReader / IWebReadService...` lines, ending
at `AddScoped<...IWebReadService, ...LmKitWebReadService>();`) and **before**
`builder.Services.AddScoped<AgentToolGateway>();`:

```csharp
// Native document tools (disabled by default — see DocumentToolsOptions). Options
// bound from "DocumentTools". Pure LM-Kit.NET document APIs (PdfForm / PdfRedactor /
// OfficeRedactor / PdfAValidator) — no model, no network, no container — so the only
// safety surface is input validation (size caps, magic-byte sniffing, term-count cap)
// applied before LM-Kit is touched, plus strictly owner-scoped output. When disabled
// the services report IsEnabled=false and every endpoint returns 501.
builder.Services.Configure<LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsOptions>(builder.Configuration.GetSection(LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsOptions.SectionName));
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Documents.IPdfFormService, LmKitOmniApi.Infrastructure.AI.Documents.PdfFormService>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Documents.IDocumentRedactionService, LmKitOmniApi.Infrastructure.AI.Documents.DocumentRedactionService>();
```

`UserResourceAccessService` (used by the controller for owner-scoped output) is already
registered above these lines, so no additional registration is required.

---

## 2. `appsettings.json` — configuration block (apply this)

Add this top-level block (e.g. right after the `"WebRead": { ... }` block):

```json
  "DocumentTools": {
    "Enabled": false,
    "MaxInputBytes": 26214400,
    "MaxSearchTerms": 50,
    "MaxOutputBytes": 26214400
  },
```

`26214400` = 25 MB. Set `"Enabled": true` to turn the tools on.

---

## 3. RECOMMENDED agent tool-graph wiring (NOT applied — coordinator's call)

The controller + services work and are tested **without** any of this. The following is
the recommended way to expose these as agent tools. It touches the three files the
coordinator owns (`AgentOrchestrator.cs`, `AgentActionDispatcher.cs`,
`ToolPermissionService.cs`).

### 3a. Action → tool names (`AgentOrchestrator.ActionToToolMap`)

```csharp
["READ_PDF_FORM"] = "ReadPdfForm",   // safe read
["FILL_PDF_FORM"] = "FillPdfForm",   // produces a file (write-ish, audited)
["REDACT_PDF"]    = "RedactPdf",     // produces a file (write-ish, audited)
["REDACT_OFFICE"] = "RedactOffice",  // produces a file (write-ish, audited)
["VALIDATE_PDFA"] = "ValidatePdfA",  // safe read
```

Also register the tools in the ReAct tool list in `AgentOrchestrator` (guarded by
`ActionAllowed(...)`, and only when `IPdfFormService.IsEnabled` /
`IDocumentRedactionService.IsEnabled`), e.g. a `read_pdf_form`, `fill_pdf_form`,
`redact_pdf`, `redact_office`, `validate_pdf_a` `DelegatedActionTool` each calling
`invoke("READ_PDF_FORM", q, ct)` etc.

### 3b. Dispatcher cases (`AgentActionDispatcher.ExecuteAsync`)

Inject `IPdfFormService` and `IDocumentRedactionService` into the dispatcher
(constructor + the `AgentOrchestrator` site that news it up). Recommended single-string
tool payload is a small JSON object; the dispatcher resolves the owned path with the
existing `_resources.ValidateOwnedPath`, reads the bytes, calls the service, and — for
fill/redact — writes the output into the caller's upload root and adds a `ProducedFile`
to `fileSink` so a `[FILE:]` marker is streamed (exactly as the `PYTHON` case does):

```csharp
// payload examples:
//   READ_PDF_FORM / VALIDATE_PDFA : {"path":"<owned pdf>"}          (VALIDATE_PDFA may add "level":"PdfA2b")
//   FILL_PDF_FORM                 : {"path":"<owned pdf>","values":[{"name":"...","value":"..."}],"flatten":false}
//   REDACT_PDF                    : {"path":"<owned pdf>","terms":["..."],"caseSensitive":false,"wholeWord":false}
//   REDACT_OFFICE                 : {"path":"<owned .docx/.xlsx/.pptx>","terms":["..."], ...}

case "READ_PDF_FORM":
    return await ExecuteReadPdfFormAsync(tenantId, userId, query, ct);        // -> _forms.GetFields(bytes) serialized
case "FILL_PDF_FORM":
    return await ExecuteFillPdfFormAsync(tenantId, userId, query, fileSink, ct); // -> _forms.Fill(...), persist + fileSink.Add(...)
case "REDACT_PDF":
    return await ExecuteRedactPdfAsync(tenantId, userId, query, fileSink, ct);   // -> _redaction.RedactPdf(...), persist + fileSink.Add(...)
case "REDACT_OFFICE":
    return await ExecuteRedactOfficeAsync(tenantId, userId, query, fileSink, ct);
case "VALIDATE_PDFA":
    return await ExecuteValidatePdfAAsync(tenantId, userId, query, ct);        // -> _redaction.ValidatePdfA(...) serialized
```

Each handler should:
1. `if (userId is null) return "[File access denied: user identity is required]";`
2. Parse the JSON payload; `var check = _resources.ValidateOwnedPath(tenantId, userId.Value, path);` and refuse if `!check.IsAllowed`.
3. Read `File.ReadAllBytes(check.SanitizedPath)` (respecting `MaxInputBytes`; the service re-validates anyway).
4. Call the service (it throws `DocumentToolsDisabledException` / `DocumentValidationException` — surface those as bracketed agent text, mirroring the other cases).
5. For fill/redact: persist the produced bytes under `_resources.GetUploadDirectory(tenantId, userId.Value)` with a `{Guid:N}{ext}` name and `fileSink.Add(new ProducedFile(storedName, friendlyName, contentType, size));`
6. `await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "<ToolName>", null, ct);`

Add the same whitelist entries to `AgentActionDispatcher` naturally via `ActionToToolMap`
(the `IsActionWhitelisted` helper already maps through it).

### 3c. Permissions (`ToolPermissionService`)

`RoleToolPermissions` — grant to **Admin** and **User**, never **Guest** (least
privilege; these read/derive from user-uploaded documents):

```csharp
"ReadPdfForm", "FillPdfForm", "RedactPdf", "RedactOffice", "ValidatePdfA"
```

`ApprovalRequiredTools` — **recommend NOT approval-required.** Rationale: unlike
`BrowseWeb`/`DbWrite`, none of these make network egress or mutate external state. Reads
(`ReadPdfForm`, `ValidatePdfA`) are side-effect-free. Fill/redact produce a **new**
owner-scoped file (the upload is untouched) and redaction is safety-*positive* (it
removes sensitive content) — on par with `RunPython`, which is enable-gated + audited +
rate-limited rather than approval-gated. They remain enable-gated (off by default) and
audited by the orchestrator's existing audit layer. *(If an operator considers derived
documents sensitive enough, `FillPdfForm` / `RedactPdf` / `RedactOffice` can be added to
`ApprovalRequiredTools` without any code change to the services.)*

`ToolRateLimits` (per-minute, mirroring the existing document tools):

```csharp
["ReadPdfForm"] = 20,
["ValidatePdfA"] = 20,
["FillPdfForm"] = 10,
["RedactPdf"] = 10,
["RedactOffice"] = 10,
```

---

## Verified LM-Kit 2026.8.6 API used (reflected from the shipped DLL)

- `LMKit.Document.Pdf.PdfForm.GetFields(byte[], CancellationToken)` → `PdfFormSnapshot`; `.Fill(byte[], PdfFormFillRequest, CancellationToken)` → `PdfFormFillResult`.
- `LMKit.Document.Pdf.PdfRedactor.RedactToBytes(byte[], PdfRedactionRequest, PdfRedactionOptions=null, CancellationToken)` → `PdfRedactionResult` (note: the CT parameter is named `cancellationToken`).
- `LMKit.Document.OpenXml.OfficeRedactor.RedactToBytes(byte[], string extension, OfficeRedactionRequest, OfficeRedactionOptions=null, CancellationToken)` → `OfficeRedactionResult`.
- `LMKit.Document.Pdf.PdfAValidator.Validate(byte[], PdfAValidationOptions=null, CancellationToken)` → `PdfAValidationReport`.
- Tests build real PDFs with `LMKit.Document.Conversion.MarkdownToPdf.ConvertToBytes(...)` and verify redaction removed text with `LMKit.Document.Pdf.PdfSearch.FindText(path, query, ...)` → `PdfTextSearchResult.TotalMatches`.

The native document engine loads and runs in the CI/test host: **all real-API tests
execute (0 skipped)** — redaction is verified by searching the output for the removed
token, form fill is verified by round-tripping the field value, and PDF/A validation
returns a well-formed verdict.
