import type { ReactNode } from "react";

export interface LiveRegionProps {
  readonly children: ReactNode;
  readonly politeness?: "polite" | "assertive";
  readonly atomic?: boolean;
  readonly visuallyHidden?: boolean;
}

export function LiveRegion({
  children,
  politeness = "polite",
  atomic = true,
  visuallyHidden = true,
}: LiveRegionProps) {
  return (
    <div
      className={visuallyHidden ? "live-region visually-hidden" : "live-region"}
      role={politeness === "assertive" ? "alert" : "status"}
      aria-live={politeness}
      aria-atomic={atomic}
    >
      {children}
    </div>
  );
}
