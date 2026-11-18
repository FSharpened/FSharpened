// The allowlist: the only Workers AI models this Worker will ever call, each with hard token limits (ADR-0024).
// Model ids are data, not user input: a request may name one of these or nothing; anything else is rejected.
// Availability and the free allocation of each model are Cloudflare's to change; keep this list short and reviewed.

export interface ModelSpec {
  readonly id: string;
  /** Largest input (prompt) in tokens this Worker will send. */
  readonly maxInputTokens: number;
  /** Largest completion in tokens this Worker will ask for. */
  readonly maxOutputTokens: number;
  /** Completion length when the caller does not say. */
  readonly defaultOutputTokens: number;
}

export const MODELS: readonly ModelSpec[] = [
  { id: "@cf/qwen/qwen2.5-coder-32b-instruct", maxInputTokens: 3000, maxOutputTokens: 256, defaultOutputTokens: 96 },
  { id: "@cf/meta/llama-3.1-8b-instruct", maxInputTokens: 3000, maxOutputTokens: 256, defaultOutputTokens: 96 },
];

/** Used when the request names no model and the DEFAULT_MODEL variable is unset or not on the allowlist. */
export const FALLBACK_DEFAULT_MODEL = "@cf/meta/llama-3.1-8b-instruct";

export function findModel(id: string): ModelSpec | undefined {
  return MODELS.find((m) => m.id === id);
}

/** A variable can only choose among allowlisted models; it can never add one. */
export function resolveDefaultModel(configured: string | undefined): ModelSpec {
  return (configured ? findModel(configured) : undefined) ?? findModel(FALLBACK_DEFAULT_MODEL)!;
}

/** Conservative token estimate for code: about 3 characters per token, rounded up. Over-counting is safe, under-counting is not. */
export function estimateTokens(chars: number): number {
  return Math.ceil(chars / 3);
}
