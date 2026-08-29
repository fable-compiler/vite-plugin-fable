import type { FableConfiguration, PluginOptions, ResolvedPluginOptions } from "./types.js";

const defaults: ResolvedPluginOptions = {
  jsx: null,
  noReflection: false,
  exclude: [],
  // The env var is the switch you can flip without editing a config, and it also turns on
  // the daemon's own log viewer.
  debug: isTruthy(process.env.VITE_PLUGIN_FABLE_DEBUG),
};

function isTruthy(value: string | undefined): boolean {
  return !!value && value !== "0" && value !== "false";
}

const jsxValues: ReadonlyArray<string> = ["automatic", "transform"];
const configurationValues: ReadonlyArray<string> = ["Debug", "Release"];

/** Every key {@link PluginOptions} accepts, used to reject anything else. */
const knownKeys: ReadonlyArray<keyof PluginOptions> = [
  "fsproj",
  "jsx",
  "noReflection",
  "exclude",
  "configuration",
  "debug",
];

function fail(message: string): never {
  throw new Error(`vite-plugin-fable: ${message}`);
}

/** Levenshtein distance, only used to suggest what a misspelled key probably meant. */
function distance(a: string, b: string): number {
  const rows: number[][] = Array.from({ length: a.length + 1 }, (): number[] =>
    new Array<number>(b.length + 1).fill(0),
  );
  for (let i = 0; i <= a.length; i++) rows[i]![0] = i;
  for (let j = 0; j <= b.length; j++) rows[0]![j] = j;
  for (let i = 1; i <= a.length; i++) {
    for (let j = 1; j <= b.length; j++) {
      const cost: number = a[i - 1] === b[j - 1] ? 0 : 1;
      rows[i]![j] = Math.min(
        rows[i - 1]![j]! + 1,
        rows[i]![j - 1]! + 1,
        rows[i - 1]![j - 1]! + cost,
      );
    }
  }
  return rows[a.length]![b.length]!;
}

function suggest(key: string): string {
  const near: string | undefined = knownKeys.find(
    (known: keyof PluginOptions): boolean => distance(key, known) <= 3,
  );
  return near ? ` Did you mean "${near}"?` : "";
}

/**
 * Applies the defaults to whatever the user put in their Vite config, rejecting anything the
 * plugin does not understand.
 *
 * A `vite.config.js` gets no type checking, so a misspelled option would otherwise be merged in,
 * ignored, and leave the user wondering why the setting had no effect.
 */
export function resolveOptions(userConfig: PluginOptions | undefined): ResolvedPluginOptions {
  if (userConfig === undefined || userConfig === null) return { ...defaults };
  if (typeof userConfig !== "object" || Array.isArray(userConfig)) {
    fail(
      `expected an options object, got ${Array.isArray(userConfig) ? "an array" : typeof userConfig}.`,
    );
  }

  for (const key of Object.keys(userConfig)) {
    if (!knownKeys.includes(key as keyof PluginOptions)) {
      fail(`unknown option "${key}".${suggest(key)} Known options: ${knownKeys.join(", ")}.`);
    }
  }

  const { fsproj, jsx, noReflection, exclude, configuration, debug } = userConfig;

  if (fsproj !== undefined && typeof fsproj !== "string") {
    fail(`"fsproj" must be a path, got ${typeof fsproj}.`);
  }
  // A JavaScript config can still pass it, and it used to be a documented value.
  if ((jsx as string | null | undefined) === "preserve") {
    // Every configuration of it fails, just at different points: Vite's oxc pass forces
    // `lang: "js"` for a `.fs` id and the JSX becomes a parse error, and with that pass out of the
    // way import analysis rejects the module for the same reason. Better to say so here.
    fail(
      `"jsx" cannot be "preserve": Vite cannot import a .fs module with JSX left in it. Use "automatic" or "transform".`,
    );
  }
  if (jsx !== undefined && jsx !== null && !jsxValues.includes(jsx as string)) {
    fail(`"jsx" must be one of ${jsxValues.join(", ")} or null, got ${JSON.stringify(jsx)}.`);
  }
  if (noReflection !== undefined && typeof noReflection !== "boolean") {
    fail(`"noReflection" must be a boolean, got ${typeof noReflection}.`);
  }
  if (debug !== undefined && typeof debug !== "boolean") {
    fail(`"debug" must be a boolean, got ${typeof debug}.`);
  }
  if (
    exclude !== undefined &&
    (!Array.isArray(exclude) || exclude.some((e: unknown): boolean => typeof e !== "string"))
  ) {
    fail(`"exclude" must be an array of strings.`);
  }
  if (configuration !== undefined && !configurationValues.includes(configuration)) {
    fail(
      `"configuration" must be ${configurationValues.join(" or ")}, got ${JSON.stringify(configuration)}.`,
    );
  }

  return {
    ...defaults,
    ...(fsproj === undefined ? {} : { fsproj }),
    ...(jsx === undefined ? {} : { jsx }),
    ...(noReflection === undefined ? {} : { noReflection }),
    ...(debug === undefined ? {} : { debug }),
    ...(exclude === undefined ? {} : { exclude }),
    ...(configuration === undefined ? {} : { configuration: configuration as FableConfiguration }),
  };
}
