import type { CompileResult, Diagnostic, ProjectFileData } from "./types.js";

/**
 * Decoding for what the daemon answers over JSON-RPC.
 *
 * This is one half of a contract; the other half is `Wire.serializerOptions` in
 * `Fable.Daemon/Types.fs`. `tests/fixtures/*.json` is what that half produces, and both sides are
 * tested against those files, so a rename on either side fails a test rather than arriving here as
 * `undefined` and breaking something later.
 *
 * The checks are hand-written rather than a schema library. Three response shapes do not pay for a
 * runtime dependency in every consumer's install, and a schema would be no less a mirror of the
 * daemon's types than these functions are: what makes drift loud is the shared fixtures, not the
 * validator.
 */

function fail(method: string, problem: string): never {
  throw new Error(`The Fable daemon answered ${method} with an unexpected shape: ${problem}.`);
}

function asObject(method: string, value: unknown, what: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    fail(method, `${what} is ${Array.isArray(value) ? "an array" : typeof value}, not an object`);
  }
  return value as Record<string, unknown>;
}

function asString(method: string, value: unknown, what: string): string {
  if (typeof value !== "string") fail(method, `${what} is ${typeof value}, not a string`);
  return value;
}

function asNumber(method: string, value: unknown, what: string): number {
  if (typeof value !== "number") fail(method, `${what} is ${typeof value}, not a number`);
  return value;
}

function asStringArray(method: string, value: unknown, what: string): string[] {
  if (!Array.isArray(value)) fail(method, `${what} is ${typeof value}, not an array`);
  return value.map((entry: unknown, index: number): string =>
    asString(method, entry, `${what}[${index}]`),
  );
}

/** Compiled JavaScript keyed by source path, an F# `Map` on the wire. */
function asStringRecord(method: string, value: unknown, what: string): Record<string, string> {
  const record: Record<string, unknown> = asObject(method, value, what);
  const decoded: Record<string, string> = {};
  for (const [key, entry] of Object.entries(record)) {
    decoded[key] = asString(method, entry, `${what}[${JSON.stringify(key)}]`);
  }
  return decoded;
}

function asDiagnostics(method: string, value: unknown, what: string): Diagnostic[] {
  if (!Array.isArray(value)) fail(method, `${what} is ${typeof value}, not an array`);
  return value.map((entry: unknown, index: number): Diagnostic => {
    const at: string = `${what}[${index}]`;
    const diagnostic: Record<string, unknown> = asObject(method, entry, at);
    // The range is read for the browser overlay's code frame, so an absent one would surface as a
    // `TypeError` from `@babel/code-frame` rather than as a bad response.
    const range: Record<string, unknown> = asObject(method, diagnostic.range, `${at}.range`);
    return {
      errorNumberText: asString(method, diagnostic.errorNumberText, `${at}.errorNumberText`),
      message: asString(method, diagnostic.message, `${at}.message`),
      severity: asString(method, diagnostic.severity, `${at}.severity`),
      fileName: asString(method, diagnostic.fileName, `${at}.fileName`),
      range: {
        startLine: asNumber(method, range.startLine, `${at}.range.startLine`),
        startColumn: asNumber(method, range.startColumn, `${at}.range.startColumn`),
        endLine: asNumber(method, range.endLine, `${at}.range.endLine`),
        endColumn: asNumber(method, range.endColumn, `${at}.range.endColumn`),
      },
    };
  });
}

/**
 * The fields of a `Success`, or a throw.
 *
 * An `Error` case carries the daemon's own message, which is the one worth showing: it already
 * says what went wrong inside the compiler.
 */
function success(method: string, response: unknown): Record<string, unknown> {
  // The daemon serialises a union case as the case name under `case` and its fields as a named
  // object under `fields`.
  const envelope: Record<string, unknown> = asObject(method, response, "the response");
  const kind: string = asString(method, envelope.case, "case");
  if (kind === "Error") {
    const fields: Record<string, unknown> = asObject(method, envelope.fields, "fields");
    throw new Error(asString(method, fields.error, "fields.error"));
  }
  if (kind !== "Success") fail(method, `case is ${JSON.stringify(kind)}`);
  return asObject(method, envelope.fields, "fields");
}

/** Decodes a `fable/project-changed` response. */
export function decodeProjectChanged(response: unknown): ProjectFileData {
  const method = "fable/project-changed";
  const fields: Record<string, unknown> = success(method, response);
  return {
    sourceFiles: asStringArray(method, fields.sourceFiles, "sourceFiles"),
    diagnostics: asDiagnostics(method, fields.diagnostics, "diagnostics"),
    dependentFiles: asStringArray(method, fields.dependentFiles, "dependentFiles"),
  };
}

/** Decodes a `fable/initial-compile` response. */
export function decodeInitialCompile(response: unknown): Record<string, string> {
  const method = "fable/initial-compile";
  const fields: Record<string, unknown> = success(method, response);
  return asStringRecord(method, fields.compiledFSharpFiles, "compiledFSharpFiles");
}

/** Decodes a `fable/compile` response. */
export function decodeCompile(response: unknown): CompileResult {
  const method = "fable/compile";
  const fields: Record<string, unknown> = success(method, response);
  return {
    compiledFiles: asStringRecord(method, fields.compiledFSharpFiles, "compiledFSharpFiles"),
    diagnostics: asDiagnostics(method, fields.diagnostics, "diagnostics"),
  };
}
