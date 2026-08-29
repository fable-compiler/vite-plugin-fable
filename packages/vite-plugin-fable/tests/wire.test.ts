import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { decodeCompile, decodeInitialCompile, decodeProjectChanged } from "../src/wire.js";
import type { CompileResult, Diagnostic, ProjectFileData } from "../src/types.js";

/**
 * The other half of these tests is `WireTests` in `Fable.Daemon.Tests/DebugTests.fs`, which asserts
 * the daemon serialises to exactly these files. Renaming a field on either side fails one of the
 * two suites, which is the point: the decoding below cannot quietly stop matching what is sent.
 */
const fixtures: string = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "fixtures");

function fixture(name: string): unknown {
  return JSON.parse(readFileSync(path.join(fixtures, name), "utf8"));
}

const diagnostic: Diagnostic = {
  errorNumberText: "FS0025",
  message: "Incomplete pattern matches on this expression.",
  severity: "Warning",
  fileName: "/project/Math.fs",
  range: { startLine: 3, startColumn: 4, endLine: 3, endColumn: 9 },
};

describe("wire", () => {
  test("decodes a fable/project-changed response", () => {
    const decoded: ProjectFileData = decodeProjectChanged(fixture("project-changed.json"));
    expect(decoded).toEqual({
      sourceFiles: ["/project/Math.fs", "/project/Library.fs"],
      diagnostics: [diagnostic],
      dependentFiles: ["/project/App.fsproj"],
    });
  });

  test("decodes a fable/initial-compile response", () => {
    expect(decodeInitialCompile(fixture("initial-compile.json"))).toEqual({
      "/project/Math.fs": "export const sum = 1;",
    });
  });

  test("decodes a fable/compile response", () => {
    const decoded: CompileResult = decodeCompile(fixture("compile.json"));
    expect(decoded).toEqual({
      compiledFiles: { "/project/Math.fs": "export const sum = 2;" },
      diagnostics: [diagnostic],
    });
  });

  test("raises the daemon's own message for an Error response", () => {
    // The daemon already says what went wrong inside the compiler, so that is the message worth
    // showing rather than one this side invents.
    expect(() => decodeProjectChanged(fixture("error.json"))).toThrow(
      "Could not crack the project.",
    );
  });

  test("names the field when a response does not match", () => {
    // Silently accepting this is how a renamed field used to travel as `undefined` and surface
    // much later as something else being broken.
    expect(() =>
      decodeProjectChanged({
        case: "Success",
        fields: { sourceFiles: ["/project/Math.fs"], diagnostics: [], dependentFiles: "nope" },
      }),
    ).toThrow(
      "The Fable daemon answered fable/project-changed with an unexpected shape: dependentFiles is string, not an array.",
    );
  });

  test("names the field when a diagnostic is missing its range", () => {
    expect(() =>
      decodeCompile({
        case: "Success",
        fields: {
          compiledFSharpFiles: {},
          diagnostics: [{ errorNumberText: "FS1", message: "m", severity: "Error", fileName: "f" }],
        },
      }),
    ).toThrow("diagnostics[0].range is undefined, not an object");
  });
});
