import type { QcmErrorPayload } from "../platform/contracts";
import type { MessageKey } from "./index";

const ERROR_KEYS: Readonly<Record<string, MessageKey>> = {
  QCM_CONFIG_PARSE_UNREADABLE: "Rewrite_ErrorConfig",
  QCM_CONFIG_PARSE_TOO_LARGE: "Rewrite_ErrorConfig",
  QCM_CONFIG_VALIDATION_BLOCKING: "Rewrite_ErrorConfig",
  QCM_PROFILE_UNKNOWN_SESSION: "Rewrite_ErrorProfile",
  QCM_PROFILE_REVISION_CONFLICT: "Rewrite_ErrorProfile",
  QCM_PROFILE_NOTHING_TO_UNDO: "Rewrite_ErrorProfile",
  QCM_PROFILE_OPERATION_REJECTED: "Rewrite_ErrorProfile",
  QCM_PROFILE_NEEDS_SAVE_TARGET: "Rewrite_ErrorProfile",
  QCM_PROFILE_SAVE_TARGET_ON_DEVICE: "Rewrite_ErrorProfile",
  QCM_DEVICE_NOT_FOUND: "Rewrite_ErrorDevice",
  QCM_DEVICE_STALE: "Rewrite_ErrorDevice",
  QCM_DEVICE_BUSY: "Rewrite_ErrorDevice",
  QCM_DEVICE_NOT_QUADSTICK: "Rewrite_ErrorDevice",
  QCM_DEVICE_REMOVED_DURING_WRITE: "Rewrite_ErrorStorage",
  QCM_STORAGE_PERMISSION_DENIED: "Rewrite_ErrorStorage",
  QCM_STORAGE_READ_ONLY: "Rewrite_ErrorStorage",
  QCM_STORAGE_FULL: "Rewrite_ErrorStorage",
  QCM_STORAGE_BACKUP_FAILED: "Rewrite_ErrorStorage",
  QCM_STORAGE_VERIFY_FAILED: "Rewrite_ErrorStorage",
  QCM_STORAGE_RESTORE_FAILED: "Rewrite_ErrorStorage",
  QCM_STORAGE_SWAP_FAILED: "Rewrite_ErrorStorage",
  QCM_STORAGE_NAME_REJECTED: "Rewrite_ErrorStorage",
  QCM_STORAGE_PROTECTED_FILE: "Rewrite_ErrorStorage",
  QCM_STORAGE_FILE_NOT_FOUND: "Rewrite_ErrorStorage",
  QCM_STORAGE_IO: "Rewrite_ErrorStorage",
  QCM_CONFIRMATION_REQUIRED: "Rewrite_ErrorConfirmation",
  QCM_CONFIRMATION_UNKNOWN: "Rewrite_ErrorConfirmation",
  QCM_CONFIRMATION_EXPIRED: "Rewrite_ErrorConfirmation",
  QCM_CONFIRMATION_MISMATCH: "Rewrite_ErrorConfirmation",
  QCM_CONFIRMATION_ALREADY_USED: "Rewrite_ErrorConfirmation",
  QCM_REQUEST_MALFORMED: "Rewrite_ErrorRequest",
  QCM_REQUEST_TOO_LARGE: "Rewrite_ErrorRequest",
  QCM_REQUEST_OUT_OF_RANGE: "Rewrite_ErrorRequest",
  QCM_CANCELLED: "Rewrite_ErrorCancelled",
  QCM_INTERNAL: "Rewrite_ErrorInternal",
};

export function localizedErrorMessage(
  error: QcmErrorPayload,
  t: (key: MessageKey, values?: readonly unknown[]) => string,
): string {
  const key = ERROR_KEYS[error.code];
  return key === undefined ? t("Rewrite_ErrorInternal") : t(key);
}

export const LOCALIZED_ERROR_CODES = Object.freeze(Object.keys(ERROR_KEYS));
