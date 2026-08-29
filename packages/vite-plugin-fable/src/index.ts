import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { normalizePath, transformWithOxc } from "vite";
import type { HotPayload, Logger, ModuleNode, Plugin, ResolvedConfig } from "vite";
import { filter, map, bufferTime, Subject } from "rxjs";
import colors from "picocolors";
// https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Promise/withResolvers
import withResolvers from "promise.withresolvers";
import { codeFrameColumns } from "@babel/code-frame";
import { startDaemon } from "./daemon.js";
import type {
  DaemonLogger,
  Diagnostic,
  FableDaemon,
  HookEvent,
  PendingChangesState,
  PluginOptions,
  PluginState,
  ProjectFileData,
} from "./types.js";

withResolvers.shim();

const fsharpFileRegex = /\.(fs|fsx)$/;
if (process.env.VITE_PLUGIN_FABLE_DEBUG) {
  console.log(`Running daemon in debug mode, visit http://localhost:9014 to view logs`);
}

const defaultConfig: PluginOptions = { jsx: null, noReflection: false, exclude: [] };

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
    config: Object.assign({}, defaultConfig, userConfig),
    compilableFiles: new Map(),
    sourceFiles: new Set(),
    fsproj: null,
    configuration: "Debug",
    dependentFiles: new Set(),
    logger: { info: console.log, warn: console.warn, error: console.error } as unknown as Logger,
    daemon: null,
    pendingChanges: null,
    hotPromiseWithResolvers: null,
    isBuild: false,
  };

  const pendingChangesSubject: Subject<HookEvent> = new Subject<HookEvent>();

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
    state.logger.warn(colors.red(`[fable] ${prefix}: ${message}`), {
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
      project: state.fsproj,
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
    state.sourceFiles.forEach((file: string) => {
      addWatchFile(file);
      const normalizedFileName: string = normalizePath(file);
      state.compilableFiles.set(normalizedFileName, compiledFSharpFiles[file]);
    });
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
    }
  }

  /**
   * F# files part of state.compilableFiles have changed.
   */
  async function fsharpFileChanged(files: string[]): Promise<Diagnostic[]> {
    try {
      const { compiledFiles, diagnostics } = await requireDaemon().compile(files);

      logDebug("fsharpFileChanged", `\n${Object.keys(compiledFiles).join("\n")} compiled`);

      for (const [key, value] of Object.entries(compiledFiles)) {
        const normalizedFileName: string = normalizePath(key);
        state.compilableFiles.set(normalizedFileName, value);
      }

      logDiagnostics(diagnostics);
      return diagnostics;
    } catch (e) {
      logCritical(
        "watchChange",
        `compilation of ${files} failed, plugin could not handle this gracefully. ${e}`,
      );
      return [];
    }
  }

  function reducePendingChange(acc: PendingChangesState, e: HookEvent): PendingChangesState {
    if (e.type === "FSharpFileChanged") {
      return {
        projectChanged: acc.projectChanged,
        fsharpFiles: acc.fsharpFiles.add(e.file),
        projectFiles: acc.projectFiles,
      };
    } else if (e.type === "ProjectFileChanged") {
      return {
        projectChanged: true,
        fsharpFiles: acc.fsharpFiles,
        projectFiles: acc.projectFiles.add(e.file),
      };
    } else {
      logWarn("pendingChanges", `Unexpected pending change ${e}`);
      return acc;
    }
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
      state.configuration = resolvedConfig.env.MODE === "production" ? "Release" : "Debug";
      state.isBuild = resolvedConfig.command === "build";
      logDebug("configResolved", `Configuration: ${state.configuration}`);
      const configDir: string | undefined =
        resolvedConfig.configFile && path.dirname(resolvedConfig.configFile);

      if (state.config && state.config.fsproj) {
        state.fsproj = state.config.fsproj;
      } else {
        state.fsproj = await findFsProjFile(configDir);
      }

      if (!state.fsproj) {
        logCritical("configResolved", `No .fsproj file was found in ${configDir}`);
      } else {
        logInfo("configResolved", `Entry fsproj ${state.fsproj}`);
      }
    },
    buildStart: async function () {
      try {
        logInfo("buildStart", "Starting daemon");
        state.daemon = createDaemon({
          info: (message: string): void => logInfo("daemon", message),
          error: (message: string): void => logError("daemon", message),
        });
        process.once("SIGINT", onSigint);

        if (state.isBuild) {
          await projectChanged(this.addWatchFile.bind(this), new Set([state.fsproj]));
        } else {
          state.pendingChanges = pendingChangesSubject
            .pipe(
              bufferTime(50),
              map((events: HookEvent[]): PendingChangesState => {
                return events.reduce(reducePendingChange, {
                  projectChanged: false,
                  fsharpFiles: new Set<string>(),
                  projectFiles: new Set<string>(),
                });
              }),
              filter(
                (pending: PendingChangesState): boolean =>
                  pending.projectChanged || pending.fsharpFiles.size > 0,
              ),
            )
            .subscribe(async (pendingChanges: PendingChangesState): Promise<void> => {
              let diagnostics: Diagnostic[] = [];

              if (pendingChanges.projectChanged) {
                await projectChanged(this.addWatchFile.bind(this), pendingChanges.projectFiles);
              } else {
                const files: string[] = Array.from(pendingChanges.fsharpFiles);
                logDebug("subscribe", files.join("\n"));
                diagnostics = await fsharpFileChanged(files);
              }

              if (state.hotPromiseWithResolvers) {
                state.hotPromiseWithResolvers.resolve(diagnostics);
                state.hotPromiseWithResolvers = null;
              }
            });

          logDebug("buildStart", "Initial project file change!");
          state.hotPromiseWithResolvers = Promise.withResolvers<Diagnostic[]>();
          pendingChangesSubject.next({
            type: "ProjectFileChanged",
            file: state.fsproj,
          });
          await state.hotPromiseWithResolvers.promise;
        }
      } catch (e) {
        logCritical("buildStart", `Unexpected failure during buildStart: ${e}`);
      }
    },
    transform: {
      filter: { id: fsharpFileRegex },
      async handler(src, id) {
        logDebug("transform", id);
        if (state.compilableFiles.has(id)) {
          let code: string | undefined = state.compilableFiles.get(id);
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
            code: code,
            map: null,
          };
        } else {
          logWarn("transform", `${id} is not part of compilableFiles.`);
        }
      },
    },
    watchChange: async function (id) {
      if (state.sourceFiles.size !== 0 && state.dependentFiles.has(id)) {
        pendingChangesSubject.next({ type: "ProjectFileChanged", file: id });
      }
    },
    handleHotUpdate: async function ({ file, server, modules }): Promise<ModuleNode[] | void> {
      if (state.compilableFiles.has(file)) {
        logDebug("handleHotUpdate", `enter for ${file}`);
        pendingChangesSubject.next({
          type: "FSharpFileChanged",
          file: file,
        });

        // handleHotUpdate could be called concurrently because multiple files changed.
        if (!state.hotPromiseWithResolvers) {
          state.hotPromiseWithResolvers = Promise.withResolvers<Diagnostic[]>();
        }

        // The idea is to wait for a shared promise to resolve.
        // This will resolve in the subscription of state.changedFSharpFiles
        const diagnostics: Diagnostic[] = await state.hotPromiseWithResolvers.promise;
        logDebug("handleHotUpdate", `leave for ${file}`);

        const errorDiagnostic: Diagnostic | undefined = diagnostics.find(
          (diag: Diagnostic): boolean => diag.severity === "Error",
        );
        if (errorDiagnostic) {
          const msg: HotPayload = await makeHmrError(errorDiagnostic);
          server.hot.send(msg);
          return [];
        } else {
          // Potentially a file that is not imported in the current graph was changed.
          // Vite should not try and hot update that module.
          return modules.filter((m: ModuleNode): boolean => m.importers.size !== 0);
        }
      }
    },
    buildEnd: () => {
      logInfo("buildEnd", "Closing daemon");
      process.off("SIGINT", onSigint);
      state.daemon?.dispose();
      state.daemon = null;
      if (state.pendingChanges) {
        state.pendingChanges.unsubscribe();
      }
    },
  };
}
