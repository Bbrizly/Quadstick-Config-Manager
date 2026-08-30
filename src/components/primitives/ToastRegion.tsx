export interface ToastMessage {
  readonly id: string;
  readonly message: string;
}

export interface ToastRegionProps {
  readonly messages: readonly ToastMessage[];
}

export function ToastRegion({ messages }: ToastRegionProps) {
  return (
    <div className="toast-region" role="status" aria-live="polite" aria-atomic="false">
      {messages.map((toast) => (
        <div className="toast" key={toast.id}>
          {toast.message}
        </div>
      ))}
    </div>
  );
}
