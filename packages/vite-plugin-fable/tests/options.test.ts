import { describe, expect, test } from "bun:test";
import { resolveOptions } from "../src/options.js";
import type { PluginOptions, ResolvedPluginOptions } from "../src/types.js";

describe("resolveOptions", () => {
  test("fills in the defaults when nothing is given", () => {
    const resolved: ResolvedPluginOptions = resolveOptions(undefined);
    expect(resolved).toEqual({ jsx: null, noReflection: false, exclude: [] });
  });

  test("keeps what the user set", () => {
    const resolved: ResolvedPluginOptions = resolveOptions({
      jsx: "automatic",
      noReflection: true,
      exclude: ["Some.Plugin"],
      configuration: "Debug",
    });
    expect(resolved.jsx).toBe("automatic");
    expect(resolved.noReflection).toBe(true);
    expect(resolved.exclude).toEqual(["Some.Plugin"]);
    expect(resolved.configuration).toBe("Debug");
  });

  test("rejects a misspelled option and suggests the right one", () => {
    // A `vite.config.js` has no type checking, so this would otherwise be silently ignored.
    expect(() => resolveOptions({ noRefleciton: true } as unknown as PluginOptions)).toThrow(
      /unknown option "noRefleciton".*Did you mean "noReflection"\?/,
    );
  });

  test("lists the known options for an unrecognisable key", () => {
    expect(() => resolveOptions({ wat: 1 } as unknown as PluginOptions)).toThrow(
      /Known options: fsproj, jsx, noReflection, exclude, configuration/,
    );
  });

  test("rejects a wrong configuration value", () => {
    expect(() => resolveOptions({ configuration: "release" } as unknown as PluginOptions)).toThrow(
      /"configuration" must be Debug or Release/,
    );
  });

  test("rejects a jsx value it does not understand", () => {
    expect(() => resolveOptions({ jsx: "babel" } as unknown as PluginOptions)).toThrow(
      /"jsx" must be one of automatic, transform or null/,
    );
  });

  test("rejects jsx: preserve, which cannot produce an importable module", () => {
    // It used to be documented, and it fails late and confusingly: Vite's oxc pass turns the JSX
    // left in a `.fs` id into a parse error, and without that pass import analysis rejects it.
    expect(() => resolveOptions({ jsx: "preserve" } as unknown as PluginOptions)).toThrow(
      /"jsx" cannot be "preserve"/,
    );
  });

  test("rejects exclude that is not a list of strings", () => {
    expect(() => resolveOptions({ exclude: "Some.Plugin" } as unknown as PluginOptions)).toThrow(
      /"exclude" must be an array of strings/,
    );
    expect(() => resolveOptions({ exclude: [1] } as unknown as PluginOptions)).toThrow(
      /"exclude" must be an array of strings/,
    );
  });

  test("rejects a non-object", () => {
    expect(() => resolveOptions("App.fsproj" as unknown as PluginOptions)).toThrow(
      /expected an options object, got string/,
    );
  });

  test("accepts null the way an absent argument is accepted", () => {
    expect(resolveOptions(null as unknown as PluginOptions).jsx).toBeNull();
  });
});
