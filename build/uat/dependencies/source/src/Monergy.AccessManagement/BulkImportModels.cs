using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Monergy.AccessManagement;

public sealed record ImportPreviewRequest(string Csv);
public sealed record ImportCommitRequest(long ExpectedPolicyVersion, string Csv, string ContentHash);
public sealed record ImportError(int RowNumber, string Field, string Code);
public sealed record ImportPreview(long PolicyVersion, string ContentHash, int RowCount, IReadOnlyList<ImportError> Errors)
{
    public bool Valid => Errors.Count == 0;
}
public sealed record ImportIdentityState(int RowNumber, string NormalizedEmail, string? ActorId);
public sealed record ImportResult(string Id, string ContentHash, string Status, int RowCount, long PolicyVersion,
    long? ActivatedPolicyVersion, string? ErrorCode, IReadOnlyList<ImportIdentityState> Identities);
public sealed record IdentityProvisioningRequest(string TenantId, string ImportId, int RowNumber, string NormalizedEmail, string IdempotencyKey);
public sealed record IdentityProvisioningReceipt(string TenantId, string NormalizedEmail, string ActorId, string IdempotencyKey);

// C&I must return the same tenant-bound principal for an idempotency key and must
// not create a second principal for an email already owned in that tenant.
public interface ITenantIdentityProvisioner
{
    ValueTask<IdentityProvisioningReceipt> ProvisionAsync(IdentityProvisioningRequest request, CancellationToken ct);
}

public sealed class IdentityProvisioningUnavailableException : Exception
{
    public IdentityProvisioningUnavailableException() : base("Identity provisioning is unavailable.") { }
}

// Deterministic Development fixture. No account, credential, invitation, or
// session is created. Reserved unavailable@example.test exercises failure.
public sealed class ReferenceTenantIdentityProvisioner : ITenantIdentityProvisioner
{
    public ValueTask<IdentityProvisioningReceipt> ProvisionAsync(IdentityProvisioningRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.TenantId is not ("T001" or "T002") ||
            !request.NormalizedEmail.EndsWith("@example.test", StringComparison.Ordinal) ||
            request.NormalizedEmail == "unavailable@example.test")
            throw new IdentityProvisioningUnavailableException();
        var actor = request.NormalizedEmail switch
        {
            "a100@example.test" => "A100",
            "a200@example.test" => "A200",
            "a300@example.test" => "A300",
            "a900@example.test" => "A900",
            "a901@example.test" => "A901",
            _ => "ref-" + BulkImportCsv.Hash(request.TenantId + "\n" + request.NormalizedEmail)
        };
        return ValueTask.FromResult(new IdentityProvisioningReceipt(request.TenantId, request.NormalizedEmail, actor, request.IdempotencyKey));
    }
}

public static class BulkImportCsv
{
    public const string Header = "email,role_code,group_codes";
    public const int MaximumRows = 500;
    public const int MaximumBytes = 240_000;

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static ParsedImport Parse(string? csv)
    {
        AccessInput.Require(csv is not null && Encoding.UTF8.GetByteCount(csv) <= MaximumBytes, "INVALID_IMPORT_SIZE");
        var errors = new List<ImportError>();
        var records = ReadRecords(csv!, errors);
        if (records.Count == 0 || !records[0].SequenceEqual(new[] { "email", "role_code", "group_codes" }, StringComparer.Ordinal))
            errors.Add(new(0, "header", "INVALID_IMPORT_HEADER"));
        if (records.Count <= 1 || records.Count > MaximumRows + 1)
            errors.Add(new(0, "rows", "IMPORT_ROW_LIMIT"));
        var rows = new List<ParsedRow>();
        var emails = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 1; index < records.Count && index <= MaximumRows; index++)
        {
            var fields = records[index];
            if (fields.Length != 3)
            {
                errors.Add(new(index, "row", "INVALID_COLUMN_COUNT"));
                continue;
            }
            var email = fields[0].Trim().ToLowerInvariant();
            if (!ValidEmail(email)) errors.Add(new(index, "email", "INVALID_EMAIL"));
            else if (!emails.TryAdd(email, index))
            {
                errors.Add(new(index, "email", "DUPLICATE_EMAIL"));
                errors.Add(new(emails[email], "email", "DUPLICATE_EMAIL"));
            }
            var role = NormalizeCode(fields[1], index, "role_code", errors);
            var groups = fields[2].Length == 0 ? [] : fields[2].Split('|');
            if (groups.Length > 50) errors.Add(new(index, "group_codes", "TOO_MANY_GROUPS"));
            var codes = groups.Take(50).Select(value => NormalizeCode(value, index, "group_codes", errors)).ToArray();
            if (codes.Any(string.IsNullOrEmpty)) errors.Add(new(index, "group_codes", "EMPTY_GROUP_CODE"));
            if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Length) errors.Add(new(index, "group_codes", "DUPLICATE_GROUP_CODE"));
            rows.Add(new(index, email, role, codes));
        }
        return new(Hash(csv!), Math.Max(0, records.Count - 1), rows, errors);
    }

    private static string NormalizeCode(string value, int row, string field, List<ImportError> errors)
    {
        if (value.Length == 0) return "";
        try { return AccessInput.Code(value); }
        catch (AccessAdministrationException) { errors.Add(new(row, field, "INVALID_CODE")); return ""; }
    }

    private static bool ValidEmail(string value) => value.Length is >= 3 and <= 320 &&
        value.All(c => char.IsAscii(c) && !char.IsWhiteSpace(c) && !char.IsControl(c)) &&
        MailAddress.TryCreate(value, out var address) && address.DisplayName.Length == 0 && address.Address == value &&
        address.User.Length <= 64 && !address.User.StartsWith('"') && address.Host.Contains('.');

    private static List<string[]> ReadRecords(string csv, List<ImportError> errors)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var started = false;
        for (var i = csv.StartsWith('\uFEFF') ? 1 : 0; i < csv.Length; i++)
        {
            var c = csv[i];
            started = true;
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
                continue;
            }
            if (c is ',' or '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                closed = false;
                if (c != ',')
                {
                    records.Add(fields.ToArray());
                    fields.Clear();
                    started = false;
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    if (records.Count > MaximumRows + 1) break;
                }
            }
            else if (c == '"' && field.Length == 0 && !closed) quoted = true;
            else if (closed || c == '"')
            {
                errors.Add(new(records.Count, "row", "MALFORMED_CSV"));
                return records;
            }
            else field.Append(c);
        }
        if (quoted) errors.Add(new(records.Count, "row", "MALFORMED_CSV"));
        if (started) { fields.Add(field.ToString()); records.Add(fields.ToArray()); }
        return records;
    }

    internal sealed record ParsedRow(int Number, string Email, string RoleCode, string[] GroupCodes);
    internal sealed record ParsedImport(string ContentHash, int RowCount, List<ParsedRow> Rows, List<ImportError> Errors);
}
