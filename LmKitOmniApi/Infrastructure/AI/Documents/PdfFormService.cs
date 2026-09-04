using LMKit.Document.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Default <see cref="IPdfFormService"/>. Thin, hermetically-testable wrapper over
/// LM-Kit.NET's static <c>PdfForm</c>: it enforces the enable gate and the up-front
/// input validation (size cap + PDF magic bytes) and maps LM-Kit's result types onto
/// the transport DTOs. No model, no network — everything here runs offline.
/// </summary>
public sealed class PdfFormService : IPdfFormService
{
    private readonly DocumentToolsOptions _options;
    private readonly ILogger<PdfFormService> _logger;

    public PdfFormService(IOptions<DocumentToolsOptions> options, ILogger<PdfFormService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public PdfFormFieldsDto GetFields(byte[] pdfData, CancellationToken ct = default)
    {
        DocumentInputValidator.EnsureEnabled(_options.Enabled);
        DocumentInputValidator.EnsureWithinInputLimit(pdfData, _options.MaxInputBytes);
        DocumentInputValidator.EnsurePdf(pdfData);

        var snapshot = PdfForm.GetFields(pdfData, ct);

        var fields = (snapshot.Fields ?? Array.Empty<PdfFormFieldInfo>())
            .Select(field => new PdfFormFieldDto(
                Name: field.Name ?? string.Empty,
                Label: field.Label ?? string.Empty,
                Kind: field.Kind.ToString(),
                Value: field.Value ?? string.Empty,
                Options: field.Options?.ToList() ?? new List<string>(),
                IsRequired: field.IsRequired,
                IsReadOnly: field.IsReadOnly,
                PageIndex: field.PageIndex))
            .ToList();

        _logger.LogInformation("📄 [PdfForm] Read {Count} field(s) (HasForm={HasForm}).", fields.Count, snapshot.HasForm);
        return new PdfFormFieldsDto(snapshot.HasForm, fields);
    }

    public (byte[] Data, PdfFormFillReportDto Report) Fill(
        byte[] pdfData,
        IEnumerable<(string Name, string Value)> values,
        bool flatten,
        CancellationToken ct = default)
    {
        DocumentInputValidator.EnsureEnabled(_options.Enabled);
        DocumentInputValidator.EnsureWithinInputLimit(pdfData, _options.MaxInputBytes);
        DocumentInputValidator.EnsurePdf(pdfData);

        var request = new PdfFormFillRequest { Flatten = flatten };
        foreach (var (name, value) in values ?? Enumerable.Empty<(string, string)>())
        {
            if (string.IsNullOrEmpty(name)) continue;
            request.Values.Add(new PdfFormFieldValue(name, value ?? string.Empty));
        }

        var result = PdfForm.Fill(pdfData, request, ct);

        var data = result.Data ?? Array.Empty<byte>();
        DocumentInputValidator.EnsureWithinOutputLimit(data, _options.MaxOutputBytes);

        var report = result.Report;
        var issues = (report?.Issues ?? Array.Empty<PdfFormFieldIssue>())
            .Select(issue => $"{issue.Name}: {issue.Reason}")
            .ToList();

        _logger.LogInformation(
            "📄 [PdfForm] Filled {Set} field(s), skipped {Skipped}, flattened={Flattened}.",
            report?.FieldsSet ?? 0, report?.FieldsSkipped ?? 0, report?.Flattened ?? false);

        return (data, new PdfFormFillReportDto(
            FieldsSet: report?.FieldsSet ?? 0,
            FieldsSkipped: report?.FieldsSkipped ?? 0,
            Flattened: report?.Flattened ?? false,
            Issues: issues));
    }
}
