/**
 * Numbers as a person reads them on a phone.
 *
 * Deliberately not `toLocaleString`: the browser's idea of a decimal separator would make two phones in the same
 * room disagree about the same transfer, and a size is a quantity rather than prose.
 */
export function size(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes;
  let index = -1;
  do {
    value /= 1024;
    index += 1;
  } while (value >= 1024 && index < units.length - 1);
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[index]}`;
}

/** Seconds as `m:ss`, which is the only shape that fits in a row beside a filename. */
export function clock(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0:00";
  const total = Math.round(seconds);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}`;
}

/** A fraction for a bar, clamped so a total that shrank cannot draw past the end. */
export function percent(bytes: number, total: number): number {
  if (!(total > 0)) return 0;
  return Math.min(100, Math.max(0, (bytes * 100) / total));
}
