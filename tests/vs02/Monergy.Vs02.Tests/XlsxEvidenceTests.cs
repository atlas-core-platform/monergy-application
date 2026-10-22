using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;
public sealed class XlsxEvidenceTests
{
    private const string SheetPath = "xl/worksheets/sheet1.xml";
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static byte[] Template() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "financial-information.xlsx"));
    private static Dictionary<string, byte[]> Parts()
    {
        using var stream = new MemoryStream(Template()); using var zip = new ZipArchive(stream);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var part = e.Open(); using var copy = new MemoryStream(); part.CopyTo(copy); return copy.ToArray(); }, StringComparer.Ordinal);
    }
    private static XElement Xml(Dictionary<string, byte[]> parts, string path = SheetPath) => XElement.Parse(Encoding.UTF8.GetString(parts[path]));
    private static void Store(Dictionary<string, byte[]> parts, XElement xml, string path = SheetPath) => parts[path] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
    private static byte[] Package(Dictionary<string, byte[]> parts, bool duplicate = false)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, bytes) in parts)
            { var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var output = entry.Open(); output.Write(bytes); }
            if (duplicate) { using var output = zip.CreateEntry(SheetPath).Open(); output.Write(parts[SheetPath]); }
        }
        return stream.ToArray();
    }
    private static EvidenceContent Source(byte[] bytes) => new(new EvidenceReference("evidence-xlsx", "document-xlsx", "version-xlsx", "customer-001",
        "reference://xlsx", Convert.ToHexStringLower(SHA256.HashData(bytes)), XlsxFinancialDocumentExtractor.ContentType), "", bytes);
    private static XElement Cell(XElement sheet, string address) => sheet.Descendants(Main + "c").Single(e => (string?)e.Attribute("r") == address);
    private static void TextCell(XElement cell, string value)
    { cell.SetAttributeValue("t", "inlineStr"); cell.RemoveNodes(); cell.Add(new XElement(Main + "is", new XElement(Main + "t", value))); }
    [Fact]
    public async Task IntakeAcceptsNonTemplateMultiSheetWorkbookWithoutClaimingFinancialExtraction()
    {
        var parts = Parts(); var workbook = Xml(parts, "xl/workbook.xml");
        var sheet = new XElement(workbook.Element(Main + "sheets")!.Elements().Single());
        sheet.SetAttributeValue("name", "General notes"); sheet.SetAttributeValue("sheetId", "2");
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"; sheet.SetAttributeValue(rel + "id", "general-sheet"); workbook.Element(Main + "sheets")!.Add(sheet); Store(parts, workbook, "xl/workbook.xml");
        var links = Xml(parts, "xl/_rels/workbook.xml.rels"); links.Add(new XElement(links.Name.Namespace + "Relationship", new XAttribute("Id", "general-sheet"), new XAttribute("Type", rel.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet2.xml"))); Store(parts, links, "xl/_rels/workbook.xml.rels");
        var types = Xml(parts, "[Content_Types].xml"); types.Add(new XElement(types.Name.Namespace + "Override", new XAttribute("PartName", "/xl/worksheets/sheet2.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))); Store(parts, types, "[Content_Types].xml");
        parts["xl/worksheets/sheet2.xml"] = parts[SheetPath]; var bytes = Package(parts);
        Assert.True(XlsxEvidenceValidation.Valid(bytes));
        await Assert.ThrowsAsync<DocumentExtractionException>(() => new XlsxFinancialDocumentExtractor().ExtractAsync(Source(bytes), "multi", default));
    }
    [Theory][InlineData("zip-only")][InlineData("missing-workbook")][InlineData("missing-sheet")][InlineData("duplicate")][InlineData("traversal")][InlineData("macro")][InlineData("xml-entity")][InlineData("wrong-sheet")]
    public void IntakeRejectsDisguisedOrMalformedWorkbookBeforeScanAndStorage(string mode)
    {
        var parts = Parts();
        if (mode == "zip-only") parts = new() { ["arbitrary.txt"] = "not a workbook"u8.ToArray() };
        if (mode == "missing-workbook") parts.Remove("xl/workbook.xml");
        if (mode == "missing-sheet") parts.Remove(SheetPath);
        if (mode == "traversal") parts["../outside"] = [1];
        if (mode == "macro") parts["xl/vbaProject.bin"] = [1];
        if (mode == "xml-entity") parts["xl/workbook.xml"] = Encoding.UTF8.GetBytes("<!DOCTYPE workbook [<!ENTITY x SYSTEM 'file:///must-not-read'>]><workbook>&x;</workbook>");
        if (mode == "wrong-sheet") parts[SheetPath] = "<not-a-worksheet/>"u8.ToArray();
        Assert.False(XlsxEvidenceValidation.Valid(Package(parts, mode == "duplicate")));
    }
    [Fact]
    public async Task ActualDownloadTemplatePreservesExplicitValuesOriginalSourceAndDeterministicRowIdentity()
    {
        var source = Source(Template()); var extractor = new StructuredDocumentExtractor();
        var fact = Assert.Single(await extractor.ExtractAsync(source, "xlsx-processing", default));
        Assert.Equal(125000m, fact.CandidateValue); Assert.Equal(new DateOnly(2026, 10, 1), fact.EffectiveDate);
        Assert.Equal("INCOME", fact.FactType); Assert.Equal("INR", fact.Currency);
        Assert.StartsWith("XLSX sheet1 row 2:", fact.SourceLocation, StringComparison.Ordinal);
        Assert.Equal(source.Reference.DocumentVersionId, fact.DocumentVersionId); Assert.Equal(source.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(XlsxFinancialDocumentExtractor.ExtractionVersion, fact.ExtractionVersion);
        Assert.Equal(fact, Assert.Single(await extractor.ExtractAsync(source, "xlsx-processing", default)));
        Assert.NotEqual(fact.SourceFactId, Assert.Single(await extractor.ExtractAsync(source with { Reference = source.Reference with { DocumentVersionId = "other-version" } }, "xlsx-processing", default)).SourceFactId);
    }
    [Fact]
    public async Task IndependentlySavedWorkbookWithThemeAndOfficeMetadataKeepsEditedValues()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "financial-information-saved.xlsx"));
        var fact = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(Source(bytes), "independent-save", default));
        Assert.Equal("Independently saved salary", fact.Label); Assert.Equal(91234.56m, fact.CandidateValue);
        Assert.Equal(new DateOnly(2026, 10, 1), fact.EffectiveDate);
    }
    [Theory]
    [InlineData("INCOME")][InlineData("EXPENSE")][InlineData("LOAN")][InlineData("ASSET")][InlineData("INVESTMENT")][InlineData("INSURANCE")][InlineData("TAX")]
    public async Task CopiesTheSevenSupportedCategoriesWithoutCalculation(string category)
    {
        var parts = Parts(); var sheet = Xml(parts); TextCell(Cell(sheet, "A2"), category); Store(parts, sheet);
        var fact = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(Source(Package(parts)), "categories", default));
        Assert.Equal(category, fact.FactType); Assert.Equal(125000m, fact.CandidateValue);
    }
    [Fact]
    public async Task SharedStringsAndReorderedCaseInsensitiveHeadersKeepExactMeanings()
    {
        var parts = Parts(); var sheet = Xml(parts); var first = Cell(sheet, "A1"); var last = Cell(sheet, "F1");
        var valueFirst = Cell(sheet, "A2"); var valueLast = Cell(sheet, "F2");
        TextCell(first, "source"); TextCell(last, "CATEGORY"); TextCell(valueFirst, "Synthetic source"); TextCell(valueLast, "expense");
        var strings = new List<string>();
        foreach (var cell in sheet.Descendants(Main + "c").Where(c => (string?)c.Attribute("t") == "inlineStr"))
        {
            var value = cell.Element(Main + "is")!.Value; var index = strings.Count; strings.Add(value);
            cell.SetAttributeValue("t", "s"); cell.RemoveNodes(); cell.Add(new XElement(Main + "v", index));
        }
        parts["xl/sharedStrings.xml"] = Encoding.UTF8.GetBytes(new XElement(Main + "sst", strings.Select(v => new XElement(Main + "si", new XElement(Main + "t", v)))).ToString()); Store(parts, sheet);
        var fact = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(Source(Package(parts)), "shared", default));
        Assert.Equal("EXPENSE", fact.FactType); Assert.EndsWith("Synthetic source", fact.SourceLocation, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("formula")][InlineData("hidden-row")][InlineData("hidden-sheet")][InlineData("hidden-column")]
    [InlineData("external")][InlineData("macro")][InlineData("traversal")][InlineData("unknown-part")][InlineData("duplicate-part")]
    [InlineData("merged")][InlineData("numeric-style")][InlineData("custom-format")][InlineData("shared-index")][InlineData("date-serial")]
    [InlineData("extra-column")][InlineData("duplicate-cell")][InlineData("wrong-row")][InlineData("wrong-target")]
    [InlineData("dtd")][InlineData("malformed")][InlineData("expanded")][InlineData("ratio")]
    [InlineData("scientific")][InlineData("precision")][InlineData("bad-date")][InlineData("duplicate-header")][InlineData("invalid-tail")]
    public async Task UnsafeOrUnsupportedWorkbookNeverReturnsAPartialValidPrefix(string mutation)
    {
        var parts = Parts(); var sheet = Xml(parts);
        switch (mutation)
        {
            case "formula": Cell(sheet, "C2").Add(new XElement(Main + "f", "1+2")); break;
            case "hidden-row": sheet.Descendants(Main + "row").Last().SetAttributeValue("hidden", "1"); break;
            case "hidden-sheet":
                var book = Xml(parts, "xl/workbook.xml"); book.Descendants(Main + "sheet").Single().SetAttributeValue("state", "hidden"); Store(parts, book, "xl/workbook.xml"); break;
            case "hidden-column": sheet.Descendants(Main + "col").First().SetAttributeValue("hidden", "1"); break;
            case "external":
                var links = Xml(parts, "xl/_rels/workbook.xml.rels"); links.Elements().First().SetAttributeValue("TargetMode", "External"); Store(parts, links, "xl/_rels/workbook.xml.rels"); break;
            case "macro": parts["xl/vbaProject.bin"] = [1, 2, 3]; break;
            case "traversal": parts["../outside.xml"] = "<x/>"u8.ToArray(); break;
            case "unknown-part": parts["xl/calcChain.xml"] = "<x/>"u8.ToArray(); break;
            case "merged": sheet.Add(new XElement(Main + "mergeCells")); break;
            case "numeric-style": Cell(sheet, "C2").SetAttributeValue("s", "1"); break;
            case "custom-format":
                var styles = Xml(parts, "xl/styles.xml"); styles.Element(Main + "cellXfs")!.Elements().First().SetAttributeValue("numFmtId", "165"); Store(parts, styles, "xl/styles.xml"); break;
            case "shared-index": Cell(sheet, "A2").SetAttributeValue("t", "s"); Cell(sheet, "A2").RemoveNodes(); Cell(sheet, "A2").Add(new XElement(Main + "v", "999")); break;
            case "date-serial": Cell(sheet, "E2").SetAttributeValue("t", "n"); Cell(sheet, "E2").RemoveNodes(); Cell(sheet, "E2").Add(new XElement(Main + "v", "45200")); break;
            case "extra-column": sheet.Descendants(Main + "row").Last().Add(new XElement(Main + "c", new XAttribute("r", "G2"))); break;
            case "duplicate-cell": Cell(sheet, "F2").SetAttributeValue("r", "E2"); break;
            case "wrong-row": sheet.Descendants(Main + "row").Last().SetAttributeValue("r", "3"); break;
            case "wrong-target":
                var relationships = Xml(parts, "xl/_rels/workbook.xml.rels"); relationships.Elements().First().SetAttributeValue("Target", "worksheets/sheet2.xml"); Store(parts, relationships, "xl/_rels/workbook.xml.rels"); break;
            case "scientific": Cell(sheet, "C2").Element(Main + "v")!.Value = "1e3"; break;
            case "precision": Cell(sheet, "C2").Element(Main + "v")!.Value = "0.123456789012345678901234567890"; break;
            case "bad-date": TextCell(Cell(sheet, "E2"), "2026-02-30"); break;
            case "duplicate-header": TextCell(Cell(sheet, "F1"), "Category"); break;
            case "invalid-tail":
                var tail = new XElement(sheet.Descendants(Main + "row").Last()); tail.SetAttributeValue("r", "3");
                foreach (var cell in tail.Elements()) cell.SetAttributeValue("r", ((string)cell.Attribute("r")!)[..1] + "3");
                TextCell(tail.Elements().ElementAt(1), "Invalid later row"); TextCell(tail.Elements().ElementAt(2), "not-money"); sheet.Element(Main + "sheetData")!.Add(tail); break;
        }
        Store(parts, sheet);
        if (mutation == "dtd") parts[SheetPath] = Encoding.UTF8.GetBytes("<!DOCTYPE worksheet [<!ENTITY x SYSTEM 'file:///not-readable'>]>" + Encoding.UTF8.GetString(parts[SheetPath]));
        if (mutation == "malformed") parts[SheetPath] = parts[SheetPath][..^10];
        if (mutation == "expanded") parts[SheetPath] = new byte[524_289];
        if (mutation == "ratio") parts[SheetPath] = Encoding.UTF8.GetBytes("<x>" + new string('a', 400_000) + "</x>");
        await Assert.ThrowsAsync<DocumentExtractionException>(() => new StructuredDocumentExtractor().ExtractAsync(Source(Package(parts, mutation == "duplicate-part")), "invalid-xlsx", default));
    }
    [Fact]
    public async Task EmptyDamagedOversizedWrongMediaAndConflictingContentFailClosedAndCancellationPropagates()
    {
        var source = Source(Template()); var extractor = new StructuredDocumentExtractor();
        foreach (var bad in new[] { source with { BinaryContent = null }, Source([1, 2, 3]), Source(new byte[1_048_577]),
            source with { Content = "untrusted text" }, Source(Template()[..^12]), source with { Reference = source.Reference with { ContentType = "application/zip" } } })
            await Assert.ThrowsAsync<DocumentExtractionException>(() => extractor.ExtractAsync(bad, "bad-source", default));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(source, "cancel", cancelled.Token));
    }
    [Fact]
    public async Task OversizedAndDuplicateDataBatchesAreNotTruncatedIntoCandidates()
    {
        foreach (var duplicate in new[] { false, true })
        {
            var parts = Parts(); var sheet = Xml(parts); var data = sheet.Element(Main + "sheetData")!; var prototype = new XElement(data.Elements().Last());
            for (var n = 3; n <= (duplicate ? 3 : 102); n++)
            {
                var row = new XElement(prototype); row.SetAttributeValue("r", n);
                foreach (var cell in row.Elements()) cell.SetAttributeValue("r", ((string)cell.Attribute("r")!)[..1] + n.ToString(CultureInfo.InvariantCulture));
                if (!duplicate) TextCell(row.Elements().ElementAt(1), "Record " + n.ToString(CultureInfo.InvariantCulture)); data.Add(row);
            }
            Store(parts, sheet);
            await Assert.ThrowsAsync<DocumentExtractionException>(() => new StructuredDocumentExtractor().ExtractAsync(Source(Package(parts)), "bounds", default));
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RealOwnerHttpPreservesOriginalBytesTenantHashAndWholeFileReviewAdoption(bool invalidTail)
    {
        var bytes = Template();
        if (invalidTail) { var parts = Parts(); var sheet = Xml(parts); TextCell(Cell(sheet, "E2"), "not-a-date"); Store(parts, sheet); bytes = Package(parts); }
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "LOCAL", ["Monergy:ProductIntake:EvidenceToken"] = new string('a', 64),
            ["Monergy:ProductIntake:ProcessingToken"] = new string('b', 64), ["Monergy:TenantBoundary:CustomerTenants:customer-001"] = "T001" });
        var store = new ReferenceEvidenceContentStore(); var repository = new InMemoryEvidenceRepository();
        builder.Services.AddSingleton(store); builder.Services.AddSingleton<IEvidenceIntakeStore>(store); builder.Services.AddSingleton<IEvidenceRepository>(repository); builder.Services.AddSingleton<IEvidenceMalwareScanner, FixtureMalwareScanner>();
        await using var host = builder.Build(); host.MapEvidenceReferenceIntake(builder.Configuration); await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new(host.Urls.Single()) }; client.DefaultRequestHeaders.Add("X-Monergy-Product-Token", new string('a', 64));
        var response = await client.PostAsJsonAsync("/internal/local/v1/evidence/content", new ReferenceEvidenceContentRequest(new string('c', 32), Convert.ToBase64String(bytes), XlsxFinancialDocumentExtractor.ContentType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var descriptor = (await response.Content.ReadFromJsonAsync<EvidenceContentDescriptor>())!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.Sha256);
        var telemetry = new CapturingTelemetry(); var evidence = new EvidenceApplication(repository, store, telemetry, TimeProvider.System);
        Assert.Equal(ContractOutcome.Success, (await evidence.CreateDocumentVersionAsync(Vs02TestContext.Request(Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload() with { OriginalFileName = "financial.xlsx", DeclaredContentType = XlsxFinancialDocumentExtractor.ContentType,
                ContentReference = descriptor.Reference, ContentSha256 = descriptor.Sha256 }, "create-xlsx", "create-xlsx"))).Outcome);
        var config = new ConfigurationManager { ["Monergy:DocumentProcessing:EvidenceUrl"] = client.BaseAddress.AbsoluteUri, ["Monergy:DocumentProcessing:EvidenceToken"] = new string('b', 64) };
        using var reader = new HttpEvidenceContentReader(config); var security = Vs02TestContext.Security() with { Workload = new WorkloadContext("job-management", "job-management:T001") };
        var source = await reader.ReadAsync("document-version-001", "customer-001", security, "read-xlsx", default);
        Assert.Equal(bytes, source?.BinaryContent); Assert.Equal("", source?.Content); Assert.Equal(descriptor.Sha256, source?.Reference.ContentSha256);
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => reader.ReadAsync("document-version-001", "customer-001", security with { Workload = new WorkloadContext("job-management", "job-management:T002") }, "foreign-tenant", default));
        var processingRepository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(processingRepository, reader, new StructuredDocumentExtractor(), new ReferenceExecutionPolicy(), telemetry, TimeProvider.System);
        await processing.ProcessDocumentAsync(Vs02TestContext.Request(Vs02ContractNames.ProcessDocument, new ProcessDocument("processing-xlsx", "document-version-001", "evidence-001", "customer-001", DateTimeOffset.UtcNow), "process-xlsx", "process-xlsx", security));
        var result = (await processingRepository.GetAsync("processing-xlsx", default))!;
        Assert.Equal(invalidTail ? ProcessingState.Failed : ProcessingState.Completed, result.State); Assert.Equal(invalidTail ? 0 : 1, result.Facts.Count);
        if (!invalidTail)
        {
            var financialRepository = new InMemoryFinancialProfileRepository(); var financial = new FinancialProfileApplication(financialRepository, telemetry, TimeProvider.System);
            var normalized = await financial.NormalizeSourceFactsAsync(Vs02TestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
                new NormalizeSourceFacts("profile-xlsx", "customer-001", "processing-xlsx", result.Facts, "provider-neutral-normalization/1.0.0", DateTimeOffset.UtcNow), "review-xlsx", "review-xlsx"));
            Assert.Equal(ContractOutcome.Success, normalized.Outcome); var fact = Assert.Single(normalized.Data!.Facts);
            var provenance = await financial.GetFinancialProvenanceAsync(Vs02TestContext.Request(Vs02ContractNames.GetFinancialProvenance, new GetFinancialProvenance(fact.FinancialProvenanceId, "customer-001"), "source-xlsx"));
            Assert.Equal("document-version-001", provenance.Data?.DocumentVersionId); Assert.Equal(XlsxFinancialDocumentExtractor.ExtractionVersion, provenance.Data?.ExtractionVersion);
        }
        using var mismatch = await client.PostAsJsonAsync("/internal/local/v1/evidence/content", new ReferenceEvidenceContentRequest(new string('d', 32), Convert.ToBase64String("not-a-zip"u8.ToArray()), XlsxFinancialDocumentExtractor.ContentType));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode); client.DefaultRequestHeaders.Clear();
        using var forbidden = await client.PostAsJsonAsync("/internal/local/v1/evidence/content", new ReferenceEvidenceContentRequest(new string('e', 32), Convert.ToBase64String(bytes), XlsxFinancialDocumentExtractor.ContentType));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await host.StopAsync();
    }
}
