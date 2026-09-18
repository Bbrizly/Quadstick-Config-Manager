export interface RenameDeviceProfileReceipt {
  readonly deviceId: string;
  readonly from: string;
  readonly to: string;
  readonly backup: string;
}
