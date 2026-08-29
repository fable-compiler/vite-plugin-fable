import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { createFilter, normalizePath, transformWithOxc } from "vite";
import type {
  DevEnvironment,
  EnvironmentModuleNode,
  HotPayload,
  Logger,
  Plugin,
  ResolvedConfig,
  ViteDevServer,
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

/** `VITE_PLUGIN_FABLE_DEBUG` also starts the daemon's log viewer, which the option does not. */
const daemonLogViewerEnabled: boolean =
  !!process.env.VITE_PLUGIN_FABLE_DEBUG &&
  process.env.VITE_PLUGIN_FABLE_DEBUG !== "0" &&
  process.env.VITE_PLUGIN_FABLE_DEBUG !== "false";

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
    root: "",
  };

  /**
   * A path as Vite would print it: relative to the root, like its own output. Paths outside the
   * root stay absolute, because a pile of `../..` is worse than the real thing.
   */
  function short(file: string): string {
    if (!state.root) return file;
    const rel: string = path.relative(state.root, file);
    return rel && !rel.startsWith("..") ? rel : file;
  }

  /**
   * Detail that only helps when something is wrong: every hook, every file transformed, where
   * `fable-library` was found. Off unless the `debug` option or `VITE_PLUGIN_FABLE_DEBUG` is set,
   * which is the difference from the old `logDebug` — that one dimmed the colour and printed
   * anyway, so there was no way to turn any of this off.
   */
  function logDebug(prefix: string, message: string): void {
    if (!state.config.debug) return;
    state.logger.info(colors.dim(`[fable] ${prefix}: ${message}`), {
      timestamp: true,
    });
  }

  /** Something the user asked for and always wants: a finished compile. */
  function logInfo(message: string): void {
    state.logger.info(`${colors.green("[fable]")} ${message}`, {
      timestamp: true,
    });
  }

  function logWarn(message: string): void {
    state.logger.warn(colors.yellow(`[fable] ${message}`), {
      timestamp: true,
    });
  }

  function logError(message: string): void {
    state.logger.error(colors.red(`[fable] ${message}`), {
      timestamp: true,
    });
  }

  /** Seconds to two decimals, the way Vite reports its own timings. */
  function since(start: number): string {
    return `${((performance.now() - start) / 1000).toFixed(2)}s`;
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
          logError(formatDiagnostic(diagnostic));
          break;
        case "warning":
          logWarn(formatDiagnostic(diagnostic));
          break;
        default:
          logInfo(formatDiagnostic(diagnostic));
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
    const started: number = performance.now();
    logDebug("compileProject", `full compile of ${short(requireFsproj())}`);
    const fableLibrary: string = await getFableLibrary();
    logDebug("compileProject", `fable-library at ${short(fableLibrary)}`);
    const projectResponse: ProjectFileData = await getProjectFile(fableLibrary);
    logDebug("compileProject", `type-checked in ${since(started)}`);
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
    logInfo(`compiled ${short(requireFsproj())} in ${since(started)}`);
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
      logDebug("projectChanged", Array.from(projectFiles).map(short).join(", "));
      state.sourceFiles.clear();
      state.compilableFiles.clear();
      state.dependentFiles.clear();
      projectFailure = null;
      await compileProject(addWatchFile);
    } catch (e) {
      projectFailure = e instanceof Error ? e : new Error(String(e));
      logError(`could not compile ${Array.from(projectFiles).map(short).join(", ")}:\n${e}`);
      // A dev server keeps running so the next edit can fix it; a build must not exit 0.
      if (state.isBuild) throw e;
    }
  }

  /**
   * F# files part of state.compilableFiles have changed.
   */
  async function fsharpFileChanged(files: string[]): Promise<BatchResult> {
    const started: number = performance.now();
    try {
      const { compiledFiles, diagnostics } = await requireDaemon().compile(files);

      logDebug("fsharpFileChanged", Object.keys(compiledFiles).map(short).join(", "));

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
      logInfo(`compiled ${files.map(short).join(", ")} in ${since(started)}`);
      return { diagnostics, changedFiles, projectChanged: false };
    } catch (e) {
      logError(`could not compile ${files.map(short).join(", ")}:\n${e}`);
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
  /**
   * Settles once the project has been cracked and compiled for the first time. In dev nothing
   * awaits this before the server listens; `transform` and `hotUpdate` await it instead.
   */
  let ready: Promise<void> = Promise.resolve();
  /**
   * Why the last crack failed, or `null`. `transform` reports it so the reason reaches the browser
   * overlay, rather than the request quietly returning F# for Vite to parse as JavaScript.
   */
  let projectFailure: Error | null = null;

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
        logDebug("runBatch", files.map(short).join(", "));
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

  /**
   * The compile for one file change, shared by every environment Vite reports it to.
   *
   * `handleHMRUpdate` takes a single timestamp per change (`server/hmr.ts:472`) and then hands it
   * to `hotUpdate` once per environment (`:667-676`). Those calls arrive one after another, not
   * together, so the coalescing window has already flushed by the time the second one lands: a dev
   * server with the default `client` and `ssr` environments compiled every edit twice. Keying on
   * the timestamp Vite already computed makes one filesystem change mean one compile, while each
   * environment still resolves the resulting files against its own module graph.
   *
   * A map rather than a single entry, because fan-outs overlap. The watcher calls
   * `onFileChange(file).catch(...)` without awaiting it (`server/index.ts:960-962`), so saving two
   * files at once interleaves them, and one entry is overwritten before the first file's second
   * environment asks for it. An entry cannot be dropped once its compile settles either: an
   * environment only asks after the previous one's `hotUpdate` returned, which is already after
   * the compile that one awaited.
   */
  const sharedSourceChanges: Map<string, Promise<BatchResult>> = new Map();

  /** Room for far more overlap than one fan-out can produce; this is a window, not a cache. */
  const SHARED_SOURCE_CHANGE_LIMIT: number = 32;

  function queueSourceChangeOnce(file: string, timestamp: number): Promise<BatchResult> {
    const key = `${file}\u0000${timestamp}`;
    const pending: Promise<BatchResult> | undefined = sharedSourceChanges.get(key);
    if (pending) return pending;
    const result: Promise<BatchResult> = queueSourceChange(file);
    sharedSourceChanges.set(key, result);
    // One insert can only put it one over, and a map iterates in insertion order, so this drops
    // the oldest change still on record.
    if (sharedSourceChanges.size > SHARED_SOURCE_CHANGE_LIMIT) {
      for (const oldest of sharedSourceChanges.keys()) {
        sharedSourceChanges.delete(oldest);
        break;
      }
    }
    return result;
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

  /**
   * Fast Refresh for `.fs` is `@vitejs/plugin-react`'s to give, and it only gives it to ids its
   * `include` matches. Nothing breaks without it — editing a component reloads the page instead of
   * updating in place — so it is easy to have never worked and never be noticed.
   *
   * The check is on `jsxRefreshInclude`, which plugin-react sets from `include` whether or not the
   * React Compiler is on, rather than on whether refresh is currently enabled: that flag is also
   * false during a build and whenever `compiler: true` hands refresh to `vite:react-compiler`.
   */
  function warnIfComponentsWillNotRefresh(resolvedConfig: ResolvedConfig): void {
    if (state.isBuild || !state.config.jsx) return;
    const oxc: ResolvedConfig["oxc"] = resolvedConfig.oxc;
    // No `jsxRefreshInclude` means no plugin-react, and so no Fast Refresh to miss.
    if (!oxc || oxc.jsxRefreshInclude === undefined) return;
    const wouldRefresh: (id: string) => boolean = createFilter(
      oxc.jsxRefreshInclude,
      oxc.jsxRefreshExclude,
    );
    if (wouldRefresh(normalizePath(path.join(resolvedConfig.root, "Component.fs")))) return;
    logWarn(
      "@vitejs/plugin-react will not apply Fast Refresh to .fs files, so editing an F# component " +
        "reloads the page instead of updating in place. Add the extension to its filter: " +
        "react({ include: /\\.fs$/ }).",
    );
  }

  /** Spawns the daemon and takes ownership of it until `buildEnd`. */
  function openDaemon(): void {
    logDebug("daemon", "starting");
    // Only the env var starts the daemon's own viewer; the `debug` option is plugin-side only.
    if (daemonLogViewerEnabled) {
      logDebug("daemon", "log viewer at http://localhost:9014");
    }
    state.daemon = createDaemon({
      info: (message: string): void => logDebug("daemon", message),
      error: (message: string): void => logError(`daemon: ${message}`),
    });
    process.once("SIGINT", onSigint);
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
      state.root = normalizePath(resolvedConfig.root);
      // Keyed off the command, not `env.MODE`: `vite build --mode staging` is still a build, and
      // reading MODE there compiled the F# in Debug while bundling it as production output.
      state.configuration = state.config.configuration ?? (state.isBuild ? "Release" : "Debug");
      logDebug("configResolved", `configuration ${state.configuration}`);
      // `configFile` is optional — there may not be one at all — while `root` is always resolved.
      const projectDir: string = resolvedConfig.root;

      if (state.config.fsproj) {
        state.fsproj = state.config.fsproj;
      } else {
        state.fsproj = await findFsProjFile(projectDir);
      }

      if (!state.fsproj) {
        logError(`no .fsproj file was found in ${short(projectDir)}`);
      } else {
        logDebug("configResolved", `entry project ${short(state.fsproj)}`);
      }

      warnIfComponentsWillNotRefresh(resolvedConfig);
    },
    // Vite awaits `buildStart` before `httpServer.listen` (`server/index.ts:1104`, reached from the
    // wrapped `listen` at `:1123`), so cracking there keeps the dev server off its port for the
    // whole first compile: no URL printed, no overlay, nothing to look at. `configureServer` runs
    // earlier (`server/index.ts:1008`) and nothing awaits what is started here, so the server boots
    // straight away and the wait moves to `transform`, where a failure reaches the browser overlay.
    configureServer(server: ViteDevServer) {
      // What `this.addWatchFile` does in dev (`server/pluginContainer.ts:867` → `ensureWatchedFile`):
      // only files outside the root need adding, everything under it is watched already.
      const rootPrefix: string = `${normalizePath(server.config.root).replace(/\/$/, "")}/`;
      addWatchFile = (id: string): void => {
        if (!id.startsWith(rootPrefix)) server.watcher.add(id);
      };

      logDebug("configureServer", "initial project crack");
      // Deliberately not returned or awaited. Rejecting would be an unhandled rejection, so the
      // failure is recorded for `transform` instead.
      ready = (async (): Promise<void> => {
        try {
          openDaemon();
          await queueProjectChange(requireFsproj());
        } catch (e) {
          projectFailure = e instanceof Error ? e : new Error(String(e));
          logError(`could not start: ${e}`);
        }
      })();
    },
    buildStart: async function () {
      // A dev server starts the daemon in `configureServer`; this is the build path, where
      // blocking is right — nothing should be bundled before the F# is compiled.
      if (!state.isBuild) return;
      try {
        addWatchFile = this.addWatchFile.bind(this);
        openDaemon();
        await projectChanged(addWatchFile, new Set([requireFsproj()]));
      } catch (e) {
        logError(`could not start: ${e}`);
        throw e;
      }
    },
    // The compiled JavaScript is already in memory, so there is nothing on disk worth reading: a
    // `load` serves it directly, where a `transform` would have Vite read the whole `.fs` file
    // only to throw it away.
    load: {
      // An id can carry a query — `?worker`, or anything a plugin upstream appended — and a bare
      // `/\.fs$/` never matches those, so the F# would reach the JavaScript parser uncompiled.
      filter: {
        id: {
          include: makeIdFiltersToMatchWithQuery([fsharpFileRegex]),
          exclude: [assetQueryRegex],
        },
      },
      async handler(id) {
        logDebug("load", short(cleanUrl(id)));
        // The dev server listens before the first compile is done, so a request can arrive while
        // the project is still being cracked. This is the wait that used to sit in `buildStart`.
        await ready;
        const file: string = cleanUrl(id);
        let code: string | undefined = state.compilableFiles.get(file);
        if (code === undefined) {
          if (projectFailure) {
            // The crack this request waited for is the one that failed, so say why here: in dev
            // this is what puts the reason in the browser overlay instead of only in the terminal.
            this.error(`Fable could not compile the project: ${projectFailure.message}`);
          }
          // Loading nothing would leave Vite to read the F# off disk and parse it as JavaScript,
          // and the user would get a syntax error pointing at `module Foo` instead of the real
          // cause. A warning was not enough: the page still broke, just less clearly.
          this.error(
            `${short(file)} is not part of ${short(requireFsproj())}, so Fable did not compile it. Add a <Compile Include="..." /> for it.`,
          );
        }
        // The plugin owns the JSX transform, and has to: Vite's own oxc pass forces
        // `lang: "js"` for a non-JS extension (`plugins/oxc.ts:266-268`), so JSX inside a `.fs`
        // id is a parse error there. `@vitejs/plugin-react` does not transform per file either.
        //
        // Refresh is deliberately left off. `vite:oxc` runs over this output afterwards and
        // applies it (or `vite:react-compiler` does, when `react({ compiler: true })` is on);
        // asking for it here as well registers every component twice.
        if (state.config.jsx) {
          const runtime: "automatic" | "classic" =
            state.config.jsx === "automatic" ? "automatic" : "classic";
          const oxcResult: { code: string } = await transformWithOxc(code, id, {
            lang: "jsx",
            jsx: { runtime },
          });
          code = oxcResult.code;
        }
        return {
          code,
          // Not `null`, which would have Vite treat the JavaScript as the contents of the `.fs`
          // file. `{ mappings: "" }` is how Vite's own plugins say a map was lost.
          map: { mappings: "" as const },
          // What this is, rather than what the extension suggests. `vite:oxc` says the same
          // (`plugins/oxc.ts:330`), but only for ids `@vitejs/plugin-react` claims, so a project
          // without it would leave rolldown to infer a module type from `.fs`.
          moduleType: "js" as const,
        };
      },
    },
    // `hotUpdate` covers dev; this is what reaches the plugin under `vite build --watch`.
    watchChange: async function (id: string): Promise<void> {
      if (state.sourceFiles.size !== 0 && state.dependentFiles.has(normalizePath(id))) {
        await queueProjectChange(normalizePath(id));
      }
    },
    hotUpdate: async function ({
      type,
      file,
      modules,
      timestamp,
    }): Promise<EnvironmentModuleNode[] | void> {
      const environment: DevEnvironment = this.environment;
      // An edit can land before the first crack has named the project's files. Deciding without
      // that list would drop the change as "not ours".
      await ready;
      const normalized: string = normalizePath(file);

      // An MSBuild input changed: re-crack, then reload, because the module graph cannot express
      // "the whole project was rebuilt".
      if (state.dependentFiles.has(normalized)) {
        logDebug("hotUpdate", `project file ${short(normalized)} changed`);
        await queueProjectChange(normalized);
        environment.hot.send({ type: "full-reload" });
        return [];
      }

      const sourceFile: string | null = toSourceFile(normalized);

      // A file appearing or disappearing changes the compilation order, which only a re-crack
      // can establish. F# projects list their files, so this usually rides along with the fsproj.
      if (type !== "update") {
        if (!sourceFile && !fsharpFileRegex.test(normalized)) return;
        logDebug("hotUpdate", `${short(normalized)} was ${type}d, re-cracking`);
        await queueProjectChange(normalized);
        environment.hot.send({ type: "full-reload" });
        return [];
      }

      if (!sourceFile) return;

      logDebug("hotUpdate", `enter for ${short(sourceFile)}`);
      const result: BatchResult = await queueSourceChangeOnce(sourceFile, timestamp);
      logDebug("hotUpdate", `leave for ${short(sourceFile)}`);

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
      logDebug("buildEnd", "closing daemon");
      process.off("SIGINT", onSigint);
      state.daemon?.dispose();
      state.daemon = null;
    },
  };
}
