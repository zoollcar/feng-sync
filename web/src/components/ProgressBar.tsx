import { percent } from "../lib/format";

type Props = {
  bytes: number;
  total: number;
  /** A tone rather than a colour, so the one place a bar is drawn is also the one place it is coloured. */
  tone?: "accent" | "ok";
  className?: string;
};

/**
 * One bar, in the two shapes a transfer can honestly take.
 *
 * A measured total draws a fraction. No total draws a shimmer, because a folder arrives as an archive whose size
 * the room never agreed on and a bar stuck at a percentage nobody measured is worse than no bar at all.
 */
export function ProgressBar({ bytes, total, tone = "accent", className }: Props) {
  const known = total > 0;
  const fill = tone === "ok" ? "bg-ok" : "bg-accent";
  return (
    <div
      className={`h-1.5 w-full overflow-hidden rounded-full bg-line ${className ?? ""}`}
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={known ? Math.round(percent(bytes, total)) : undefined}
    >
      {known ? (
        <div
          className={`h-full rounded-full ${fill} transition-[width] duration-200 ease-out`}
          style={{ width: `${percent(bytes, total)}%` }}
        />
      ) : (
        <div className={`bar-slide h-full w-1/3 rounded-full ${fill}`} />
      )}
    </div>
  );
}
