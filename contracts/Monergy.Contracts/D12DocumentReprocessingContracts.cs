using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monergy.Contracts;

public static class D12ContractNames
{
    public const string ReprocessDocument = "ReprocessDocument";
    public const string DocumentProcessingFailed = "DocumentProcessingFailed";
}

[JsonConverter(typeof(ReprocessingReasonJsonConverter))]
public enum ReprocessingReason
{
    [JsonStringEnumMemberName("FAILED_PROCESSING")]
    FailedProcessing,

    [JsonStringEnumMemberName("UPDATED_EVIDENCE")]
    UpdatedEvidence,
}

public sealed record ReprocessDocument(
    string ProcessingId,
    string PreviousProcessingId,
    string CustomerId,
    string EvidenceReferenceId,
    string DocumentVersionId,
    [property: JsonRequired] ReprocessingReason Reason,
    DateTimeOffset RequestedAt);

public sealed record ReprocessDocumentResult(
    string ProcessingId,
    string PreviousProcessingId,
    ProcessingState State,
    string? FailureCode,
    bool Retryable,
    DateTimeOffset UpdatedAt);

public sealed record DocumentProcessingFailedPayload(
    string ProcessingId,
    string CustomerId,
    string EvidenceId,
    string DocumentVersionId,
    string FailureCode,
    [property: JsonRequired]
    [property: JsonConverter(typeof(DocumentProcessingFailureCategoryJsonConverter))]
    ContractErrorCategory ErrorCategory,
    bool Retryable,
    string? PreviousProcessingId,
    string AttemptId);

public sealed class ReprocessingReasonJsonConverter : JsonConverter<ReprocessingReason>
{
    public override ReprocessingReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A reprocessing reason must be one of the supported string values.");
        }

        return reader.GetString() switch
        {
            "FAILED_PROCESSING" => ReprocessingReason.FailedProcessing,
            "UPDATED_EVIDENCE" => ReprocessingReason.UpdatedEvidence,
            _ => throw new JsonException("The reprocessing reason is unsupported."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ReprocessingReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ReprocessingReason.FailedProcessing => "FAILED_PROCESSING",
            ReprocessingReason.UpdatedEvidence => "UPDATED_EVIDENCE",
            _ => throw new JsonException("The reprocessing reason is unsupported."),
        });
}

public sealed class DocumentProcessingFailureCategoryJsonConverter : JsonConverter<ContractErrorCategory>
{
    public override ContractErrorCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A document-processing failure category must be a supported string value.");
        }

        return reader.GetString() switch
        {
            "AccessDenied" => ContractErrorCategory.AccessDenied,
            "ConsentRequired" => ContractErrorCategory.ConsentRequired,
            "ConsentExpired" => ContractErrorCategory.ConsentExpired,
            "ConsentRevoked" => ContractErrorCategory.ConsentRevoked,
            "DependencyFailure" => ContractErrorCategory.DependencyFailure,
            "ProcessingFailed" => ContractErrorCategory.ProcessingFailed,
            _ => throw new JsonException("The document-processing failure category is unsupported."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContractErrorCategory value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ContractErrorCategory.AccessDenied => "AccessDenied",
            ContractErrorCategory.ConsentRequired => "ConsentRequired",
            ContractErrorCategory.ConsentExpired => "ConsentExpired",
            ContractErrorCategory.ConsentRevoked => "ConsentRevoked",
            ContractErrorCategory.DependencyFailure => "DependencyFailure",
            ContractErrorCategory.ProcessingFailed => "ProcessingFailed",
            _ => throw new JsonException("The document-processing failure category is unsupported."),
        });
}
