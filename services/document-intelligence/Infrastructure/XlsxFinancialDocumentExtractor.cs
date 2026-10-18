using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

// Deliberately bounded financial template, not an arbitrary workbook evaluator.
public sealed class XlsxFinancialDocumentExtractor : IDocumentExtractor
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string ExtractionVersion = "financial-xlsx/1.0.0";
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly HashSet<string> Parts = new(StringComparer.Ordinal)
    { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml",
        "xl/sharedStrings.xml", "xl/styles.xml", "xl/theme/theme1.xml", "docProps/core.xml", "docProps/app.xml" };
    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(EvidenceContent content, string processingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Reference.ContentType != ContentType || content.Content.Length != 0 || content.BinaryContent is not { Length: > 0 and <= 1_048_576 }) throw Unsupported();
        try
        {
            using var stream = new MemoryStream(content.BinaryContent, false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count is < 5 or > 10 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count) throw Unsupported();
            long expanded = 0;
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Parts.Contains(entry.FullName) || entry.Length is < 1 or > 524_288 || entry.CompressedLength < 1 ||
                    entry.Length > entry.CompressedLength * 100 || (expanded += entry.Length) > 1_048_576) throw Unsupported();
                entries.Add(entry.FullName, entry);
            }
            var roots = new Dictionary<string, XElement>(StringComparer.Ordinal);
            XElement Xml(string path)
            {
                if (roots.TryGetValue(path, out var cached)) return cached;
                if (!entries.TryGetValue(path, out var entry)) throw Unsupported();
                using var input = entry.Open();
                // Independently bound actual expansion, even if ZIP length metadata lies.
                using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested(); if (bytes.Length + count > 524_288) throw Unsupported(); bytes.Write(buffer, 0, count);
                }
                if (bytes.Length != entry.Length) throw Unsupported(); bytes.Position = 0;
                using var reader = XmlReader.Create(bytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 524_288 });
                var root = XElement.Load(reader); roots.Add(path, root); return root;
            }
            foreach (var path in entries.Keys) _ = Xml(path);
            var types = Xml("[Content_Types].xml");
            XNamespace typesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
            if (types.Name != typesNs + "Types" || !types.Elements(typesNs + "Override").Any(e =>
                (string?)e.Attribute("PartName") == "/xl/workbook.xml" && (string?)e.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml") ||
                types.Descendants().Attributes("ContentType").Any(a => a.Value.Contains("macro", StringComparison.OrdinalIgnoreCase))) throw Unsupported();
            if (!types.Elements(typesNs + "Override").Any(e => (string?)e.Attribute("PartName") == "/xl/worksheets/sheet1.xml" &&
                (string?)e.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml") ||
                types.Elements(typesNs + "Override").Select(e => (string?)e.Attribute("PartName")).Distinct(StringComparer.Ordinal).Count() != types.Elements(typesNs + "Override").Count()) throw Unsupported();
            var rootLinks = Xml("_rels/.rels");
            CheckRelationships(rootLinks);
            if (rootLinks.Elements(Rel + "Relationship").Count(e => (string?)e.Attribute("Type") == OfficeRel.NamespaceName + "/officeDocument" &&
                (string?)e.Attribute("Target") is "xl/workbook.xml" or "/xl/workbook.xml") != 1) throw Unsupported();
            if (rootLinks.Elements().Any(e => (string?)e.Attribute("Target") is not ("xl/workbook.xml" or "/xl/workbook.xml" or "docProps/core.xml" or "/docProps/core.xml" or "docProps/app.xml" or "/docProps/app.xml"))) throw Unsupported();
            var workbook = Xml("xl/workbook.xml");
            if (workbook.Name != Main + "workbook" || workbook.Descendants().Any(e => e.Name.LocalName is "externalReferences" or "extLst" || e.Name.LocalName == "definedNames" && e.HasElements)) throw Unsupported();
            var sheets = workbook.Element(Main + "sheets")?.Elements().ToArray();
            if (sheets is not { Length: 1 } || sheets[0].Name != Main + "sheet" || (string?)sheets[0].Attribute("state") is not (null or "visible")) throw Unsupported();
            var sheetId = (string?)sheets[0].Attribute(OfficeRel + "id"); var links = Xml("xl/_rels/workbook.xml.rels"); CheckRelationships(links);
            var sheetLinks = links.Elements(Rel + "Relationship").Where(e => (string?)e.Attribute("Type") == OfficeRel.NamespaceName + "/worksheet").ToArray();
            if (sheetLinks.Length != 1 || (string?)sheetLinks[0].Attribute("Id") != sheetId ||
                (string?)sheetLinks[0].Attribute("Target") is not ("worksheets/sheet1.xml" or "/xl/worksheets/sheet1.xml")) throw Unsupported();
            foreach (var link in links.Elements(Rel + "Relationship"))
            {
                var kind = (string?)link.Attribute("Type"); var target = (string?)link.Attribute("Target");
                if (kind == OfficeRel.NamespaceName + "/worksheet") continue;
                if (!(kind == OfficeRel.NamespaceName + "/styles" && target is "styles.xml" or "/xl/styles.xml" ||
                      kind == OfficeRel.NamespaceName + "/sharedStrings" && target is "sharedStrings.xml" or "/xl/sharedStrings.xml" ||
                      kind == OfficeRel.NamespaceName + "/theme" && target is "theme/theme1.xml" or "/xl/theme/theme1.xml")) throw Unsupported();
            }
            var strings = entries.ContainsKey("xl/sharedStrings.xml") ? ReadStrings(Xml("xl/sharedStrings.xml")) : [];
            var formats = entries.ContainsKey("xl/styles.xml") ? ReadFormats(Xml("xl/styles.xml")) : [0];
            var sheet = Xml("xl/worksheets/sheet1.xml");
            if (sheet.Name != Main + "worksheet" || sheet.Descendants().Any(e => e.Name.LocalName is
                "f" or "formula" or "mergeCells" or "hyperlinks" or "conditionalFormatting" or "dataValidations" or "extLst" or "drawing" or "legacyDrawing" or "sheetProtection") ||
                sheet.Descendants().Attributes("hidden").Any(a => a.Value is not ("0" or "false")) ||
                sheet.Descendants().Attributes("zeroHeight").Any(a => a.Value is not ("0" or "false"))) throw Unsupported();
            string[] allowedElements = ["dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "pageMargins", "pageSetup", "printOptions", "sheetPr"];
            if (sheet.Elements().Any(e => e.Name.Namespace != Main || !allowedElements.Contains(e.Name.LocalName, StringComparer.Ordinal)) ||
                sheet.Elements(Main + "sheetData").Count() != 1) throw Unsupported();
            var data = sheet.Element(Main + "sheetData") ?? throw Unsupported();
            if (sheet.Descendants(Main + "row").Count() != data.Elements().Count() || sheet.Descendants(Main + "c").Count() != data.Elements().Sum(e => e.Elements().Count())) throw Unsupported();
            var rows = new List<string[]>();
            foreach (var row in data.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowNumber = rows.Count + 1;
                if (row.Name != Main + "row" || rows.Count >= 101 || (string?)row.Attribute("r") != rowNumber.ToString(CultureInfo.InvariantCulture) || row.Attribute("s") is not null) throw Unsupported();
                var cells = row.Elements().ToArray(); if (cells.Length != 6) throw Unsupported();
                var values = new string[6];
                for (var c = 0; c < cells.Length; c++)
                {
                    var cell = cells[c]; var address = ((char)('A' + c)).ToString() + rowNumber.ToString(CultureInfo.InvariantCulture);
                    if (cell.Name != Main + "c" || (string?)cell.Attribute("r") != address || cell.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("r" or "s" or "t"))) throw Unsupported();
                    var style = Number((string?)cell.Attribute("s") ?? "0"); if (style >= formats.Length) throw Unsupported();
                    var type = (string?)cell.Attribute("t") ?? "n";
                    var children = cell.Elements().ToArray(); if (children.Length != 1) throw Unsupported();
                    string value;
                    if (type == "inlineStr" && children[0].Name == Main + "is") value = Text(children[0]);
                    else if (children[0].Name == Main + "v" && !children[0].HasElements)
                    {
                        value = children[0].Value;
                        if (type == "s") { var index = Number(value); if (index >= strings.Length) throw Unsupported(); value = strings[index]; }
                        else if (type == "n")
                        {
                            // Numeric values are allowed only in the explicit Amount column, with General formatting.
                            if (rows.Count == 0 || !rows[0][c].Trim().Equals("Amount", StringComparison.OrdinalIgnoreCase) || formats[style] != 0) throw Unsupported();
                        }
                        else if (type != "str") throw Unsupported();
                    }
                    else throw Unsupported();
                    if (value.Length > 1024 || value.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))) throw Unsupported();
                    values[c] = value;
                }
                rows.Add(values);
            }
            if (rows.Count is < 2 or > 101) throw Unsupported();
            return Task.FromResult(CsvFinancialDocumentExtractor.ValidateTable(content.Reference, processingId, rows[0], rows.Skip(1).ToArray(),
                "XLSX sheet1", "xlsx", ExtractionVersion, cancellationToken));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or XmlException or ArgumentException or OverflowException or DocumentExtractionException)
        { throw Unsupported(); }
    }
    private static void CheckRelationships(XElement root)
    {
        if (root.Name != Rel + "Relationships") throw Unsupported();
        var links = root.Elements().ToArray();
        if (links.Select(e => (string?)e.Attribute("Id")).Distinct(StringComparer.Ordinal).Count() != links.Length) throw Unsupported();
        foreach (var link in links)
        {
            var target = (string?)link.Attribute("Target");
            if (link.Name != Rel + "Relationship" || link.Attribute("TargetMode") is not null || string.IsNullOrEmpty((string?)link.Attribute("Id")) ||
                string.IsNullOrEmpty(target) || target.Contains(':') || target.Contains('\\') || target.Split('/').Any(p => p is "." or "..")) throw Unsupported();
        }
    }
    private static string[] ReadStrings(XElement root)
    {
        if (root.Name != Main + "sst" || root.Elements().Count() > 606 || root.Elements().Any(e => e.Name != Main + "si")) throw Unsupported();
        return root.Elements().Select(Text).ToArray();
    }
    private static string Text(XElement element)
    {
        var children = element.Elements().ToArray();
        if (children.Length != 1 || children[0].Name != Main + "t" || children[0].HasElements || children[0].Value.Length > 1024) throw Unsupported();
        return children[0].Value;
    }
    private static int[] ReadFormats(XElement root)
    {
        if (root.Name != Main + "styleSheet") throw Unsupported();
        if (root.Element(Main + "numFmts")?.HasElements == true || root.Element(Main + "cellStyleXfs")?.Elements().Any(e =>
                Number((string?)e.Attribute("numFmtId") ?? "0") is not (0 or 49)) == true) throw Unsupported();
        var styles = root.Element(Main + "cellXfs")?.Elements().ToArray();
        if (styles is not { Length: >= 1 and <= 32 } || styles.Any(e => e.Name != Main + "xf")) throw Unsupported();
        var formats = styles.Select(e => Number((string?)e.Attribute("numFmtId") ?? "0")).ToArray();
        if (formats.Any(n => n is not (0 or 49))) throw Unsupported();
        return formats;
    }
    private static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw Unsupported();
    private static DocumentExtractionException Unsupported() => new("The XLSX must be a single visible sheet matching the financial information template, without formulas, links or unsupported formatting.");
}
