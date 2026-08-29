import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { normalizePath, transformWithOxc } from "vite";
import type {
  DevEnvironment,
  EnvironmentModuleNode,
  HotPayload,
  Logger,
  Plugin,
  ResolvedConfig,
} from "vite";
import colors from "picocolors";
import { makeIdFiltersToMatchWithQuery } from "@rolldown/pluginutils";
import { codeFrameColumns } from "@babel/code-frame";
import { startDaemon } from "./daemon.js";
import { resolveOptions } from "./options.js";
import type {
  BatchResult,
  DaemonLogger,
  Diagnostic,
  FableDaemon,
  PluginOptions,
  PluginState,
  ProjectFileData,
} from "./types.js";

// The public type surface, so a `vite.config.ts` can name what it passes in.
export type { FableConfiguration, PluginOptions } from "./types.js";

const fsharpFileRegex = /\.(fs|fsx)$/;

/**
 * `Component.fs?raw` and `Component.fs?url` ask for the file, not the module it compiles to, and
 * Vite's asset plugin has already answered by the time a transform runs. Compiling over that
 * answer would turn `import source from "./Component.fs?raw"` into the compiled module.
 */
const assetQueryRegex = /[?&](raw|url)(?:&|$)/;

/** Vite's own `cleanUrl`, which is internal to `vite/src/shared/utils` and not exported. */
function cleanUrl(id: string): string {
  return id.replace(/[?#].*$/, "");
}

if (process.env.VITE_PLUGIN_FABLE_DEBUG) {
  console.log(`Running daemon in debug mode, visit http://localhost:9014 to view logs`);
}

/**
 * Initializes and returns a Vite plugin to process the incoming F# project.
 */
export default function fablePlugin(userConfig?: PluginOptions): Plugin {
  return createFablePlugin(userConfig, startDaemon);
}

/**
 * The plugin, with the daemon injected.
 *
 * Not part of the public API — {@link fablePlugin} is. This exists so tests can drive every hook
 * against a stub daemon, without spawning `dotnet` or compiling F# for real.
 *
 * @internal
 */
export function createFablePlugin(
  userConfig: PluginOptions | undefined,
  createDaemon: (logger: DaemonLogger) => FableDaemon,
): Plugin {
  const state: PluginState = {
    config: resolveOptions(userConfig),
    compilableFiles: new Map(),
    sourceFiles: new Set(),
    fsproj: null,
    configuration: "Debug",
    dependentFiles: new Set(),
    logger: { info: console.log, warn: console.warn, error: console.error } as unknown as Logger,
    daemon: null,
    isBuild: false,
  };

  function logDebug(prefix: string, message: string): void {
    state.logger.info(colors.dim(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  function logInfo(prefix: string, message: string): void {
    state.logger.info(colors.green(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  function logWarn(prefix: string, message: string): void {
    state.logger.warn(colors.yellow(`[fable]: ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  function logError(prefix: string, message: string): void {
    state.logger.error(colors.red(`[fable] ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  function logCritical(prefix: string, message: string): void {
    state.logger.error(colors.red(`[fable] ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /**
   * @param configDir - Folder path of the vite.config.js file.
   */
  async function findFsProjFile(configDir: string): Promise<string | null> {
    const files: string[] = await fs.readdir(configDir);
    const fsprojFiles: string[] = files
      .filter((file: string) => file && file.toLocaleLowerCase().endsWith(".fsproj"))
      .map((fsProjFile: string) => {
        // Return the full path of the .fsproj file
        return normalizePath(path.join(configDir, fsProjFile));
      });
    return fsprojFiles.length > 0 ? fsprojFiles[0] : null;
  }

  async function getFableLibrary(): Promise<string> {
    // Resolve through the module system so hoisted, isolated and pnpm-style layouts all work.
    const packageJson: string = fileURLToPath(
      import.meta.resolve("@fable-org/fable-library-js/package.json"),
    );
    return normalizePath(path.dirname(packageJson));
  }

  /**
   * Retrieves the project file. At this stage the project is type-checked but Fable did not compile anything.
   * @param fableLibrary - Location of the fable-library node module.
   */
  async function getProjectFile(fableLibrary: string): Promise<ProjectFileData> {
    return requireDaemon().projectChanged({
      configuration: state.configuration,
      project: requireFsproj(),
      fableLibrary,
      exclude: state.config.exclude,
      noReflection: state.config.noReflection,
    });
  }

  function formatDiagnostic(diagnostic: Diagnostic): string {
    return `${diagnostic.severity.toUpperCase()} ${diagnostic.errorNumberText}: ${diagnostic.message} ${diagnostic.fileName} (${diagnostic.range.startLine},${diagnostic.range.startColumn}) (${diagnostic.range.endLine},${diagnostic.range.endColumn})`;
  }

  function logDiagnostics(diagnostics: Diagnostic[]): void {
    for (const diagnostic of diagnostics) {
      switch (diagnostic.severity.toLowerCase()) {
        case "error":
          logError("", formatDiagnostic(diagnostic));
          break;
        case "warning":
          logWarn("", formatDiagnostic(diagnostic));
          break;
        default:
          logInfo("", formatDiagnostic(diagnostic));
          break;
      }
    }
  }

  /**
   * During `vite build` an F# error has to stop the build; exiting 0 with broken output is worse
   * than failing loudly. In dev the server stays up so the browser overlay can show the diagnostic.
   */
  function failBuildOnErrors(diagnostics: Diagnostic[]): void {
    if (!state.isBuild) return;
    const errors: Diagnostic[] = diagnostics.filter(
      (diagnostic: Diagnostic): boolean => diagnostic.severity.toLowerCase() === "error",
    );
    if (errors.length === 0) return;
    throw new Error(
      `F# compilation failed with ${errors.length} error(s):\n${errors.map(formatDiagnostic).join("\n")}`,
    );
  }

  /**
   * Does a type-check and compilation of the state.fsproj
   */
  async function compileProject(addWatchFile: (id: string) => void): Promise<void> {
    logInfo("compileProject", `Full compile started of ${state.fsproj}`);
    const fableLibrary: string = await getFableLibrary();
    logDebug("compileProject", `fable-library located at ${fableLibrary}`);
    logInfo("compileProject", `about to type-checked ${state.fsproj}.`);
    const projectResponse: ProjectFileData = await getProjectFile(fableLibrary);
    logInfo("compileProject", `${state.fsproj} was type-checked.`);
    logDiagnostics(projectResponse.diagnostics);
    failBuildOnErrors(projectResponse.diagnostics);
    for (const sf of projectResponse.sourceFiles) {
      state.sourceFiles.add(normalizePath(sf));
    }
    for (let dependentFile of projectResponse.dependentFiles) {
      dependentFile = normalizePath(dependentFile);
      state.dependentFiles.add(dependentFile);
      addWatchFile(dependentFile);
    }
    const compiledFSharpFiles: Record<string, string> = await requireDaemon().initialCompile();
    logInfo("compileProject", `Full compile completed of ${state.fsproj}`);
    state.sourceFiles.forEach((file: string): void => addWatchFile(file));
    // Key off what the daemon returned rather than looking each source file up in it: the two sets
    // differ (signature files are never compiled), and indexing a raw-keyed map with an
    // already-normalised path silently yields `undefined` for every entry.
    for (const [file, javaScript] of Object.entries(compiledFSharpFiles)) {
      state.compilableFiles.set(normalizePath(file), javaScript);
    }
  }

  /**
   * Either the project or a dependent file changed
   */
  async function projectChanged(
    addWatchFile: (id: string) => void,
    projectFiles: Set<string>,
  ): Promise<void> {
    try {
      logInfo("projectChanged", `dependent file ${Array.from(projectFiles).join("\n")} changed.`);
      state.sourceFiles.clear();
      state.compilableFiles.clear();
      state.dependentFiles.clear();
      await compileProject(addWatchFile);
    } catch (e) {
      logCritical(
        "projectChanged",
        `Unexpected failure during projectChanged for ${Array.from(projectFiles)},\n${e}`,
      );
      // A dev server keeps running so the next edit can fix it; a build must not exit 0.
      if (state.isBuild) throw e;
    }
  }

  /**
   * F# files part of state.compilableFiles have changed.
   */
  async function fsharpFileChanged(files: string[]): Promise<BatchResult> {
    try {
      const { compiledFiles, diagnostics } = await requireDaemon().compile(files);

      logDebug("fsharpFileChanged", `\n${Object.keys(compiledFiles).join("\n")} compiled`);

      // Fable recompiles everything downstream of the edit, but most of that output is byte for
      // byte what we already served. Reporting it anyway would drag modules that cannot accept a
      // hot update into the update, and one dead end turns the whole thing into a page reload.
      const changedFiles: string[] = [];
      for (const [key, value] of Object.entries(compiledFiles)) {
        const normalizedFileName: string = normalizePath(key);
        if (state.compilableFiles.get(normalizedFileName) !== value) {
          changedFiles.push(normalizedFileName);
        }
        state.compilableFiles.set(normalizedFileName, value);
      }

      logDiagnostics(diagnostics);
      return { diagnostics, changedFiles, projectChanged: false };
    } catch (e) {
      logCritical(
        "fsharpFileChanged",
        `compilation of ${files} failed, plugin could not handle this gracefully. ${e}`,
      );
      return { diagnostics: [], changedFiles: [], projectChanged: false };
    }
  }

  /** How long changes are collected before a compile starts. */
  const COALESCE_WINDOW_MS: number = 50;

  interface Deferred<T> {
    promise: Promise<T>;
    resolve(value: T): void;
  }

  function deferred<T>(): Deferred<T> {
    let resolve!: (value: T) => void;
    const promise: Promise<T> = new Promise<T>((r: (value: T) => void): void => {
      resolve = r;
    });
    return { promise, resolve };
  }

  /**
   * One coalescing window's worth of changes.
   *
   * Each batch carries its own promise, so a caller always learns the result of the compile its
   * file actually went into. Sharing one promise across batches meant a file changed during a slow
   * compile was answered by the previous batch's diagnostics and pushed to the browser before it
   * had been compiled at all.
   */
  interface Batch {
    fsharpFiles: Set<string>;
    projectFiles: Set<string>;
    projectChanged: boolean;
    settled: Deferred<BatchResult>;
  }

  let pendingBatch: Batch | null = null;
  /** Compiles run one at a time; the daemon holds a single project. */
  let inFlight: Promise<unknown> = Promise.resolve();
  let addWatchFile: (id: string) => void = (): void => {};

  /** Adds to the open batch, opening one (and its timer) if there is none. */
  function openBatch(): Batch {
    if (pendingBatch) return pendingBatch;
    const batch: Batch = {
      fsharpFiles: new Set(),
      projectFiles: new Set(),
      projectChanged: false,
      settled: deferred<BatchResult>(),
    };
    pendingBatch = batch;
    setTimeout((): void => {
      if (pendingBatch === batch) pendingBatch = null;
      inFlight = inFlight.then((): Promise<void> => runBatch(batch));
    }, COALESCE_WINDOW_MS);
    return batch;
  }

  async function runBatch(batch: Batch): Promise<void> {
    const result: BatchResult = {
      diagnostics: [],
      changedFiles: [],
      projectChanged: batch.projectChanged,
    };
    try {
      if (batch.projectChanged) {
        await projectChanged(addWatchFile, batch.projectFiles);
      } else {
        const files: string[] = Array.from(batch.fsharpFiles);
        logDebug("runBatch", files.join("\n"));
        const compiled: BatchResult = await fsharpFileChanged(files);
        result.diagnostics = compiled.diagnostics;
        result.changedFiles = compiled.changedFiles;
      }
    } finally {
      batch.settled.resolve(result);
    }
  }

  /** Queues an F# source change and resolves with the batch it lands in. */
  function queueSourceChange(file: string): Promise<BatchResult> {
    const batch: Batch = openBatch();
    batch.fsharpFiles.add(file);
    return batch.settled.promise;
  }

  /** Queues a full re-crack and resolves with the batch it lands in. */
  function queueProjectChange(file: string): Promise<BatchResult> {
    const batch: Batch = openBatch();
    batch.projectChanged = true;
    batch.projectFiles.add(file);
    return batch.settled.promise;
  }

  /**
   * Maps a changed path to the source file the plugin knows about, or `null` if it is not ours.
   * A `.fsi` maps to itself for compilation — the daemon walks signature to implementation — but
   * the browser imports the `.fs`, so module lookup uses {@link implementationOf}.
   */
  function toSourceFile(file: string): string | null {
    const normalized: string = normalizePath(file);
    return state.sourceFiles.has(normalized) ? normalized : null;
  }

  /** `Foo.fsi` describes the module the browser loads as `Foo.fs`. */
  function implementationOf(file: string): string {
    return file.endsWith(".fsi") ? `${file.slice(0, -4)}.fs` : file;
  }

  /**
   * The entry project, or an error. `configResolved` can fail to find one, and every later hook
   * depends on it, so this is where that turns into a real failure instead of a `null` in flight.
   */
  function requireFsproj(): string {
    if (!state.fsproj) {
      throw new Error(
        "No .fsproj was found. Set the `fsproj` plugin option, or put a project file in the Vite root.",
      );
    }
    return state.fsproj;
  }

  function requireDaemon(): FableDaemon {
    if (!state.daemon) {
      throw new Error("The Fable daemon is not running.");
    }
    return state.daemon;
  }

  /**
   * Vite only installs a SIGTERM listener, and its close path is what reaches `buildEnd`. Ctrl+C
   * signals the whole foreground process group in a terminal, but a SIGINT aimed at this process
   * alone would leave the daemon running, so clean it up here. Node stops exiting by default once a
   * SIGINT listener exists, hence the explicit exit.
   */
  function onSigint(): void {
    state.daemon?.dispose();
    state.daemon = null;
    process.exit(130);
  }

  async function makeHmrError(diagnostic: Diagnostic): Promise<HotPayload> {
    const fileContent: string = await fs.readFile(diagnostic.fileName, "utf-8");
    const frame: string = codeFrameColumns(fileContent, {
      start: {
        line: diagnostic.range.startLine,
        column: diagnostic.range.startColumn,
      },
      end: {
        line: diagnostic.range.endLine,
        column: diagnostic.range.endColumn,
      },
    });
    return {
      type: "error",
      err: {
        message: diagnostic.message,
        frame: frame,
        stack: "",
        id: diagnostic.fileName,
        loc: {
          file: diagnostic.fileName,
          line: diagnostic.range.startLine,
          column: diagnostic.range.startColumn,
        },
      },
    };
  }

  return {
    name: "vite-plugin-fable",
    enforce: "pre",
    configResolved: async function (resolvedConfig: ResolvedConfig) {
      state.logger = resolvedConfig.logger;
      state.isBuild = resolvedConfig.command === "build";
      // Keyed off the command, not `env.MODE`: `vite build --mode staging` is still a build, and
      // reading MODE there compiled the F# in Debug while bundling it as production output.
      state.configuration = state.config.configuration ?? (state.isBuild ? "Release" : "Debug");
      logDebug("configResolved", `Configuration: ${state.configuration}`);
      // `configFile` is optional — there may not be one at all — while `root` is always resolved.
      const projectDir: string = resolvedConfig.root;

      if (state.config.fsproj) {
        state.fsproj = state.config.fsproj;
      } else {
        state.fsproj = await findFsProjFile(projectDir);
      }

      if (!state.fsproj) {
        logCritical("configResolved", `No .fsproj file was found in ${projectDir}`);
      } else {
        logInfo("configResolved", `Entry fsproj ${state.fsproj}`);
      }
    },
    buildStart: async function () {
      try {
        addWatchFile = this.addWatchFile.bind(this);
        logInfo("buildStart", "Starting daemon");
        state.daemon = createDaemon({
          info: (message: string): void => logInfo("daemon", message),
          error: (message: string): void => logError("daemon", message),
        });
        process.once("SIGINT", onSigint);

        const fsproj: string = requireFsproj();
        if (state.isBuild) {
          await projectChanged(addWatchFile, new Set([fsproj]));
        } else {
          logDebug("buildStart", "Initial project crack");
          await queueProjectChange(fsproj);
        }
      } catch (e) {
        logCritical("buildStart", `Unexpected failure during buildStart: ${e}`);
        if (state.isBuild) throw e;
      }
    },
    transform: {
      // An id can carry a query — `?worker`, or anything a plugin upstream appended — and a bare
      // `/\.fs$/` never matches those, so the F# would reach the JavaScript parser uncompiled.
      filter: {
        id: {
          include: makeIdFiltersToMatchWithQuery([fsharpFileRegex]),
          exclude: [assetQueryRegex],
        },
      },
      async handler(src, id) {
        logDebug("transform", id);
        const file: string = cleanUrl(id);
        let code: string | undefined = state.compilableFiles.get(file);
        if (code !== undefined) {
          // If Fable outputted JSX, we still need to transform this.
          // @vitejs/plugin-react does not do this.
          if (state.config.jsx) {
            const runtime: "automatic" | "classic" =
              state.config.jsx === "automatic" ? "automatic" : "classic";
            const jsx: "preserve" | { runtime: "automatic" | "classic" } =
              state.config.jsx === "preserve" ? "preserve" : { runtime };
            const oxcResult: { code: string } = await transformWithOxc(code, id, {
              lang: "jsx",
              jsx,
            });
            code = oxcResult.code;
          }
          return {
            code,
            // Not `null`, which would claim the previous mapping still holds: this replaced F#
            // with JavaScript. `{ mappings: "" }` is how Vite's own plugins say a map was lost.
            map: { mappings: "" as const },
          };
        } else if (state.isBuild) {
          // Returning nothing would let Vite parse the F# source as JavaScript, and the user would
          // get a syntax error pointing at `module Foo` instead of the real cause.
          this.error(`${id} was not compiled by Fable, so it cannot be bundled.`);
        } else {
          logWarn("transform", `${id} is not part of compilableFiles.`);
        }
      },
    },
    // `hotUpdate` covers dev; this is what reaches the plugin under `vite build --watch`.
    watchChange: async function (id: string): Promise<void> {
      if (state.sourceFiles.size !== 0 && state.dependentFiles.has(normalizePath(id))) {
        await queueProjectChange(normalizePath(id));
      }
    },
    hotUpdate: async function ({ type, file, modules }): Promise<EnvironmentModuleNode[] | void> {
      const environment: DevEnvironment = this.environment;
      const normalized: string = normalizePath(file);

      // An MSBuild input changed: re-crack, then reload, because the module graph cannot express
      // "the whole project was rebuilt".
      if (state.dependentFiles.has(normalized)) {
        logDebug("hotUpdate", `project file ${normalized} changed`);
        await queueProjectChange(normalized);
        environment.hot.send({ type: "full-reload" });
        return [];
      }

      const sourceFile: string | null = toSourceFile(normalized);

      // A file appearing or disappearing changes the compilation order, which only a re-crack
      // can establish. F# projects list their files, so this usually rides along with the fsproj.
      if (type !== "update") {
        if (!sourceFile && !fsharpFileRegex.test(normalized)) return;
        logDebug("hotUpdate", `${normalized} was ${type}d, re-cracking`);
        await queueProjectChange(normalized);
        environment.hot.send({ type: "full-reload" });
        return [];
      }

      if (!sourceFile) return;

      logDebug("hotUpdate", `enter for ${sourceFile}`);
      const result: BatchResult = await queueSourceChange(sourceFile);
      logDebug("hotUpdate", `leave for ${sourceFile}`);

      const errorDiagnostic: Diagnostic | undefined = result.diagnostics.find(
        (diag: Diagnostic): boolean => diag.severity.toLowerCase() === "error",
      );
      if (errorDiagnostic) {
        environment.hot.send(await makeHmrError(errorDiagnostic));
        return [];
      }

      if (result.projectChanged) {
        environment.hot.send({ type: "full-reload" });
        return [];
      }

      // Every module whose output actually changed, not just the edited file: one edit can change
      // what Fable emits for the files downstream of it.
      const updated: Set<EnvironmentModuleNode> = new Set(modules);
      for (const changed of result.changedFiles) {
        for (const mod of environment.moduleGraph.getModulesByFile(implementationOf(changed)) ??
          []) {
          updated.add(mod);
        }
      }
      // Returning an empty array tells Vite "handled, do nothing", which would silently skip a
      // module nothing imports. Leaving it undefined lets Vite propagate and full-reload instead.
      return updated.size > 0 ? Array.from(updated) : undefined;
    },
    buildEnd: () => {
      logInfo("buildEnd", "Closing daemon");
      process.off("SIGINT", onSigint);
      state.daemon?.dispose();
      state.daemon = null;
    },
  };
}
