import { SuppressionReason } from '../models';

/**
 * Human label for a suppression reason. Defined once so the job-detail explanation and the
 * Suppressions page cannot drift. The null/undefined fallback covers a stamped recipient whose
 * reason predates the field (or an unrecognized future value).
 */
export function suppressionReasonLabel(reason: SuppressionReason | null | undefined): string {
  switch (reason) {
    case 'HardBounce':
      return 'address hard-bounced';
    case 'SpamComplaint':
      return 'recipient reported spam';
    case 'ResidentRequest':
      return 'recipient unsubscribed';
    case 'AdminAction':
      return 'suppressed by an administrator';
    case 'ProviderUnsubscribe':
      return 'unsubscribed via email provider';
    default:
      return 'on the suppression list';
  }
}
