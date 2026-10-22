using System.Text.Json;
using Monergy.Services.DocumentIntelligence.Application;
using Npgsql;
namespace Monergy.Services.DocumentIntelligence.Infrastructure;
public sealed partial class PostgresDocumentClassificationRepository : IDocumentOcrRepository
{
    public async Task SaveAsync(DocumentOcrResult result, CancellationToken ct)
    {
        if (!DocumentOcrRules.Valid(result)) throw new EvidenceDependencyException("Invalid OCR record.");
        await using var command = Source(result.TenantId).CreateCommand("INSERT INTO document_ocr(tenant_id,document_key,source_hash,result) VALUES (@tenant,@key,@hash,@result::jsonb) ON CONFLICT(tenant_id,document_key) DO UPDATE SET result=EXCLUDED.result WHERE document_ocr.source_hash=EXCLUDED.source_hash");
        Identity(command, result.TenantId, result.CustomerId, result.DocumentVersionId); command.Parameters.AddWithValue("hash", result.SourceHash); command.Parameters.AddWithValue("result", JsonSerializer.Serialize(result));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new EvidenceDependencyException("OCR original changed.");
    }
    public async Task<DocumentOcrResult?> ReadOcrAsync(string tenant, string customer, string version, CancellationToken ct)
    {
        await using var command = Source(tenant).CreateCommand("SELECT result::text FROM document_ocr WHERE tenant_id=@tenant AND document_key=@key"); Identity(command, tenant, customer, version);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<DocumentOcrResult>(json) ?? throw new InvalidOperationException("Invalid OCR record.");
    }
}
