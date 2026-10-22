using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
namespace Monergy.Services.Evidence.Infrastructure;

// Structural intake only: no formula evaluation, financial-template requirement or external-resource access.
public static class XlsxEvidenceValidation
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relations = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    public static bool Valid(byte[] bytes)
    {
        if (bytes.Length is < 22 or > 1_048_576) return false;
        try
        {
            using var input = new MemoryStream(bytes, false); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count is < 4 or > 512) return false;
            var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Length is < 1 or > 240 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                    name.TrimEnd('/').Split('/').Any(x => x is "" or "." or "..") || !parts.TryAdd(name, entry) ||
                    entry.Length > 10_485_760 || (expanded += entry.Length) > 20_971_520 || name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) return false;
                // Check every bounded compressed member, including parts not interpreted here.
                using var stream = entry.Open(); var buffer = new byte[8192]; long read = 0; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) if ((read += count) > entry.Length) return false;
                if (read != entry.Length) return false;
            }
            XElement Xml(string name)
            {
                if (!parts.TryGetValue(name, out var entry) || entry.Length > 1_048_576) throw new InvalidDataException();
                using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
                return XElement.Load(reader);
            }
            var types = Xml("[Content_Types].xml");
            if (types.Name != XName.Get("Types", Types) || types.Descendants().Attributes("ContentType").Any(x => x.Value.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) || x.Value.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) ||
                types.Elements(XName.Get("Override", Types)).Count(x => (string?)x.Attribute("PartName") == "/xl/workbook.xml" && (string?)x.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml") != 1) return false;
            var root = Xml("_rels/.rels");
            if (root.Name != XName.Get("Relationships", Relations) || root.Elements(XName.Get("Relationship", Relations)).Count(x =>
                (string?)x.Attribute("Type") == OfficeRelations + "/officeDocument" && (string?)x.Attribute("TargetMode") != "External" &&
                ((string?)x.Attribute("Target"))?.TrimStart('/') == "xl/workbook.xml") != 1) return false;
            var workbook = Xml("xl/workbook.xml"); var relationships = Xml("xl/_rels/workbook.xml.rels");
            if (workbook.Name != XName.Get("workbook", Main) || relationships.Name != XName.Get("Relationships", Relations)) return false;
            var sheets = workbook.Element(XName.Get("sheets", Main))?.Elements(XName.Get("sheet", Main)).ToArray() ?? [];
            if (sheets.Length is < 1 or > 50 || sheets.Select(x => (string?)x.Attribute("sheetId")).Distinct(StringComparer.Ordinal).Count() != sheets.Length ||
                sheets.Select(x => (string?)x.Attribute("name")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Length) return false;
            var links = relationships.Elements(XName.Get("Relationship", Relations)).ToArray();
            if (links.Any(x => string.IsNullOrWhiteSpace((string?)x.Attribute("Id"))) || links.Select(x => (string?)x.Attribute("Id")).Distinct(StringComparer.Ordinal).Count() != links.Length) return false;
            foreach (var sheet in sheets)
            {
                if (string.IsNullOrWhiteSpace((string?)sheet.Attribute("sheetId")) || string.IsNullOrWhiteSpace((string?)sheet.Attribute("name"))) return false;
                var id = (string?)sheet.Attribute(XName.Get("id", OfficeRelations));
                var link = links.SingleOrDefault(x => (string?)x.Attribute("Id") == id);
                var target = (string?)link?.Attribute("Target");
                if (link is null || (string?)link.Attribute("Type") != OfficeRelations + "/worksheet" || (string?)link.Attribute("TargetMode") == "External" || string.IsNullOrWhiteSpace(target)) return false;
                var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                if (!path.StartsWith("xl/worksheets/", StringComparison.Ordinal) || !path.EndsWith(".xml", StringComparison.Ordinal) || !parts.ContainsKey(path)) return false;
                if (types.Elements(XName.Get("Override", Types)).Count(x => (string?)x.Attribute("PartName") == "/" + path && (string?)x.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml") != 1 || Xml(path).Name != XName.Get("worksheet", Main)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or XmlException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; }
    }
}
