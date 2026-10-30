import { AccessApiError } from '../access/accessApi';

export interface DriveFailureGuidance {
  message: string;
  retainExactImport: boolean;
}

export function driveFailureGuidance(error: unknown, provider: 'Google Drive' | 'OneDrive'): DriveFailureGuidance {
  if (!(error instanceof AccessApiError)) return {
    message: `The connection could not be updated. ${provider} did not confirm this result. Refresh the connection to check its current state before retrying.`,
    retainExactImport: true,
  };
  if (error.status === 429 && error.code === 'DRIVE_RATE_LIMITED') {
    const seconds = error.retryAfterSeconds ?? 60;
    return {
      message: `${provider} asked Monergy to slow down. Wait ${String(seconds)} seconds before retrying. No result was confirmed.`,
      retainExactImport: true,
    };
  }
  if (error.code === 'DRIVE_RECONNECT_REQUIRED') return {
    message: `${provider} permission has expired. Refresh the connection, then disconnect and connect it again if permission still needs attention.`,
    retainExactImport: false,
  };
  if (error.code === 'DRIVE_PERMISSION_REQUIRED') return {
    message: `${provider} no longer allows this operation. Review the provider permission before trying again.`,
    retainExactImport: false,
  };
  if (error.status === 409 || ['DRIVE_FILE_CHANGED', 'DRIVE_FILE_UNAVAILABLE'].includes(error.code)) return {
    message: `The ${provider} connection or file changed. Refresh the connection and file list before choosing the file again.`,
    retainExactImport: false,
  };
  if (error.status === 401 || error.status === 403) return {
    message: error.message,
    retainExactImport: false,
  };
  if (error.status === 503) return {
    message: `${provider} or a required owner is temporarily unavailable. This result was not confirmed. Refresh the connection before retrying.`,
    retainExactImport: true,
  };
  return { message: error.message, retainExactImport: false };
}
