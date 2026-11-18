export interface ZipEntry {
  name: string;
  size: number;
  compressed: number;
  data(): Buffer;
}
export function readZip(buf: Buffer): ZipEntry[];
export const ALLOWED: RegExp[];
export const REQUIRED: string[];
export const MAX_BYTES: number;
export function pathPatterns(extra?: string[]): Array<[RegExp, string]>;
export function scanText(name: string, text: string, extraPaths?: string[]): string[];
export function checkEntries(
  entries: ZipEntry[],
  totalBytes: number,
  expected: { version: string; engine: string; extraPaths?: string[] },
): { problems: string[]; files: Array<{ name: string; size: number }>; total: number };
