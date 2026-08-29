import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promises as fs } from "node:fs";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import { normalizePath, transformWithOxc } from "vite";
import type { HotPayload, Logger, ModuleNode, Plugin, ResolvedConfig } from "vite";
import { filter, map, bufferTime, Subject } from "rxjs";
import colors from "picocolors";
// https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Promise/withResolvers
import withResolvers from "promise.withresolvers";
import { codeFrameColumns } from "@babel/code-frame";
import type {
  Diagnostic,
  FSharpDiscriminatedUnion,
  HookEvent,
  PendingChangesState,
  PluginOptions,
  PluginState,
  ProjectFileData,
} from "./types.js";

withResolvers.shim();

const fsharpFileRegex = /\.(fs|fsx)$/;
const currentDir = path.dirname(fileURLToPath(import.meta.url));

// The plugin is emitted to `dist/`, the daemon is published to `bin/` at the package root.
const fableDaemon = path.join(currentDir, "..", "bin", "Fable.Daemon.dll");

if (process.env.VITE_PLUGIN_FABLE_DEBUG) {
  console.log(`Running daemon in debug mode, visit http://localhost:9014 to view logs`);
}

const defaultConfig: PluginOptions = { jsx: null, noReflection: false, exclude: [] };

/**
 * Initializes and returns a Vite plugin to process the incoming F# project.
 */
export default function fablePlugin(userConfig?: PluginOptions): Plugin {
  const state: PluginState = {
    config: Object.assign({}, defaultConfig, userConfig),
    compilableFiles: new Map(),
    sourceFiles: new Set(),
    fsproj: null,
    configuration: "Debug",
    dependentFiles: new Set(),
    logger: { info: console.log, warn: console.warn, error: console.error } as unknown as Logger,
    dotnetProcess: null,
    endpoint: null,
    pendingChanges: null,
    hotPromiseWithResolvers: null,
    isBuild: false,
  };

  const pendingChangesSubject = new Subject<HookEvent>();

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
    const files = await fs.readdir(configDir);
    const fsprojFiles = files
      .filter((file) => file && file.toLocaleLowerCase().endsWith(".fsproj"))
      .map((fsProjFile) => {
        // Return the full path of the .fsproj file
        return normalizePath(path.join(configDir, fsProjFile));
      });
    return fsprojFiles.length > 0 ? fsprojFiles[0] : null;
  }

  async function getFableLibrary(): Promise<string> {
    // Resolve through the module system so hoisted, isolated and pnpm-style layouts all work.
    const packageJson = fileURLToPath(
      import.meta.resolve("@fable-org/fable-library-js/package.json"),
    );
    return normalizePath(path.dirname(packageJson));
  }

  /**
   * Retrieves the project file. At this stage the project is type-checked but Fable did not compile anything.
   * @param fableLibrary - Location of the fable-library node module.
   * @throws If the result from the endpoint is not a success case.
   */
  async function getProjectFile(fableLibrary: string): Promise<ProjectFileData> {
    const result: FSharpDiscriminatedUnion = await state.endpoint.send("fable/project-changed", {
      configuration: state.configuration,
      project: state.fsproj,
      fableLibrary,
      exclude: state.config.exclude,
      noReflection: state.config.noReflection,
    });

    if (result.case === "Success") {
      return {
        sourceFiles: result.fields[0],
        diagnostics: result.fields[1],
        dependentFiles: result.fields[2],
      };
    } else {
      throw new Error(result.fields[0] || "Unknown error occurred");
    }
  }

  /**
   * Try and compile the entire project using Fable. The daemon contains all the information at this point to do this.
   * No need to pass any additional info.
   * @throws If the result from the endpoint is not a success case.
   */
  async function tryInitialCompile(): Promise<Record<string, string>> {
    const result: FSharpDiscriminatedUnion = await state.endpoint.send("fable/initial-compile");

    if (result.case === "Success") {
      return result.fields[0];
    } else {
      throw new Error(result.fields[0] || "Unknown error occurred");
    }
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
    const fableLibrary = await getFableLibrary();
    logDebug("compileProject", `fable-library located at ${fableLibrary}`);
    logInfo("compileProject", `about to type-checked ${state.fsproj}.`);
    const projectResponse = await getProjectFile(fableLibrary);
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
    const compiledFSharpFiles = await tryInitialCompile();
    logInfo("compileProject", `Full compile completed of ${state.fsproj}`);
    state.sourceFiles.forEach((file) => {
      addWatchFile(file);
      const normalizedFileName = normalizePath(file);
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
      const compilationResult: FSharpDiscriminatedUnion = await state.endpoint.send(
        "fable/compile",
        {
          fileNames: files,
        },
      );
      if (
        compilationResult.case === "Success" &&
        compilationResult.fields &&
        compilationResult.fields.length > 0
      ) {
        const compiledFSharpFiles: Record<string, string> = compilationResult.fields[0];

        logDebug("fsharpFileChanged", `\n${Object.keys(compiledFSharpFiles).join("\n")} compiled`);

        for (const [key, value] of Object.entries(compiledFSharpFiles)) {
          const normalizedFileName = normalizePath(key);
          state.compilableFiles.set(normalizedFileName, value);
        }

        const diagnostics: Diagnostic[] = compilationResult.fields[1];
        logDiagnostics(diagnostics);
        return diagnostics;
      } else {
        logError("watchChange", `compilation of ${files} failed, ${compilationResult.fields[0]}`);
        return [];
      }
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

  async function makeHmrError(diagnostic: Diagnostic): Promise<HotPayload> {
    const fileContent = await fs.readFile(diagnostic.fileName, "utf-8");
    const frame = codeFrameColumns(fileContent, {
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
      const configDir = resolvedConfig.configFile && path.dirname(resolvedConfig.configFile);

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
        state.dotnetProcess = spawn("dotnet", [fableDaemon, "--stdio"], {
          shell: true,
          stdio: "pipe",
        });
        state.endpoint = new JSONRPCEndpoint(state.dotnetProcess.stdin, state.dotnetProcess.stdout);

        if (state.isBuild) {
          await projectChanged(this.addWatchFile.bind(this), new Set([state.fsproj]));
        } else {
          state.pendingChanges = pendingChangesSubject
            .pipe(
              bufferTime(50),
              map((events) => {
                return events.reduce(reducePendingChange, {
                  projectChanged: false,
                  fsharpFiles: new Set<string>(),
                  projectFiles: new Set<string>(),
                });
              }),
              filter((state) => state.projectChanged || state.fsharpFiles.size > 0),
            )
            .subscribe(async (pendingChanges) => {
              let diagnostics: Diagnostic[] = [];

              if (pendingChanges.projectChanged) {
                await projectChanged(this.addWatchFile.bind(this), pendingChanges.projectFiles);
              } else {
                const files = Array.from(pendingChanges.fsharpFiles);
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
          let code = state.compilableFiles.get(id);
          // If Fable outputted JSX, we still need to transform this.
          // @vitejs/plugin-react does not do this.
          if (state.config.jsx) {
            const runtime: "automatic" | "classic" =
              state.config.jsx === "automatic" ? "automatic" : "classic";
            const jsx = state.config.jsx === "preserve" ? "preserve" : { runtime };
            const oxcResult = await transformWithOxc(code, id, {
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
        const diagnostics = await state.hotPromiseWithResolvers.promise;
        logDebug("handleHotUpdate", `leave for ${file}`);

        const errorDiagnostic = diagnostics.find((diag) => diag.severity === "Error");
        if (errorDiagnostic) {
          const msg = await makeHmrError(errorDiagnostic);
          console.log(msg);
          server.hot.send(msg);
          return [];
        } else {
          // Potentially a file that is not imported in the current graph was changed.
          // Vite should not try and hot update that module.
          return modules.filter((m) => m.importers.size !== 0);
        }
      }
    },
    buildEnd: () => {
      logInfo("buildEnd", "Closing daemon");
      if (state.dotnetProcess) {
        state.dotnetProcess.kill();
      }
      if (state.pendingChanges) {
        state.pendingChanges.unsubscribe();
      }
    },
  };
}
