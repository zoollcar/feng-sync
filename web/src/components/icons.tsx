/**
 * The handful of glyphs this page draws, inline.
 *
 * No icon package: the whole application is smaller than one of those, and a font or a sprite would be a second
 * request the listener would have to serve. Each is a 24-unit stroke path scaled by the class that places it.
 */
import type { ReactNode } from "react";

type Props = { className?: string };

const base = "h-5 w-5";

function Frame({ children, className }: Props & { children: ReactNode }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      className={className ? `${base} ${className}` : base}
    >
      {children}
    </svg>
  );
}

export function IconDown({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="M12 4v12" />
      <path d="m7 11 5 5 5-5" />
      <path d="M5 20h14" />
    </Frame>
  );
}

export function IconUp({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="M12 20V8" />
      <path d="m7 13 5-5 5 5" />
      <path d="M5 4h14" />
    </Frame>
  );
}

export function IconFile({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
      <path d="M14 3v5h5" />
    </Frame>
  );
}

export function IconFolder({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="M4 7a2 2 0 0 1 2-2h3.6a2 2 0 0 1 1.4.6L12.4 7H18a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z" />
    </Frame>
  );
}

export function IconSend({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="M4.5 12 20 4l-4.5 16-4-6z" />
      <path d="m11.5 14 3-3" />
    </Frame>
  );
}

export function IconMonitor({ className }: Props) {
  return (
    <Frame className={className}>
      <rect x="3" y="4" width="18" height="12" rx="2" />
      <path d="M9 20h6" />
      <path d="M12 16v4" />
    </Frame>
  );
}

export function IconChevron({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="m6 9 6 6 6-6" />
    </Frame>
  );
}

export function IconCheck({ className }: Props) {
  return (
    <Frame className={className}>
      <path d="m5 13 4 4L19 7" />
    </Frame>
  );
}

export function IconAlert({ className }: Props) {
  return (
    <Frame className={className}>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 8v5" />
      <path d="M12 16.5v.01" />
    </Frame>
  );
}
