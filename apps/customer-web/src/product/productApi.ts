import type { AccessApi } from '../access/accessApi';

export interface ProductEvidenceReceipt {
  operationId: string;
  documentId: string;
  documentVersionId: string;
  evidenceId: string;
  version: number;
  originalFileName: string;
  contentType: string;
  contentLength: number;
  contentSha256: string;
  acceptedAt: string;
  dataDurability: 'Runtime only';
}

export const evidenceTypes = ['application/pdf', 'image/png', 'image/jpeg', 'text/plain'];
export const maximumEvidenceBytes = 1_048_576;

export async function submitEvidence(
  api: AccessApi,
  operationId: string,
  file: File,
  signal?: AbortSignal,
): Promise<ProductEvidenceReceipt> {
  const contentBase64 = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => {
      reject(new Error('The selected file could not be read.'));
    };
    reader.onload = () => {
      const value = typeof reader.result === 'string' ? reader.result : '';
      const separator = value.indexOf(',');
      if (separator < 0) reject(new Error('The selected file could not be encoded.'));
      else resolve(value.slice(separator + 1));
    };
    reader.readAsDataURL(file);
  });
  return api.product<ProductEvidenceReceipt>(
    'v1/evidence',
    'POST',
    {
      operationId,
      originalFileName: file.name,
      contentType: file.type,
      contentBase64,
    },
    signal,
  );
}
