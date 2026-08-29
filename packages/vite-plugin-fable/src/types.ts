import type { Logger } from "vite";

/** Options for an F# project. */
export interface FSharpProjectOptions {
  /** List of source files in the project. */
  sourceFiles: string[];
}

/** A range within a file, used for diagnostics or annotations. */
export interface DiagnosticRange {
  /** The start line of the diagnostic range. */
  startLine: number;
  /** The start column of the diagnostic range. */
  startColumn: number;
  /** The end line of the diagnostic range. */
  endLine: number;
  /** The end column of the diagnostic range. */
  endColumn: number;
}

/** A diagnostic message, typically an error or warning, within a file. */
export interface Diagnostic {
  /** The error number or identifier text. */
  errorNumberText: string;
  /** The descriptive diagnostic message. */
  message: string;
  /** The range within the file where the diagnostic applies. */
  range: DiagnosticRange;
  /** The severity of the diagnostic, for example `Error` or `Warning`. */
  severity: string;
  /** The name of the file containing the diagnostic. */
  fileName: string;
}

/** The MSBuild configuration the F# project is compiled with. */
export type FableConfiguration = "Debug" | "Release";

/** Options for configuring the plugin. */
export interface PluginOptions {
  /** The main fsproj to load. Defaults to the single `.fsproj` found next to the Vite config. */
  fsproj?: string;
  /**
   * Apply a JSX transformation after Fable compilation.
   *
   * `transform` uses the classic runtime, `automatic` the automatic runtime.
   *
   * There is no `preserve`: Vite cannot import a `.fs` module with JSX left in it. Its own oxc
   * pass forces `lang: "js"` for a non-JS extension, so the JSX becomes a parse error, and without
   * that pass import analysis rejects the module instead. The plugin has to finish the job.
   *
   * @see https://oxc.rs/docs/guide/usage/transformer/jsx
   */
  jsx?: "transform" | "automatic" | null;
  /** Pass `noReflection` to Fable.Compiler. */
  noReflection?: boolean;
  /** Pass `exclude` to Fable.Compiler. */
  exclude?: string[];
  /**
   * Print what the plugin is doing: every hook it runs, every file it transforms, where it found
   * `fable-library`, and the daemon's own chatter.
   *
   * Off by default, where the plugin prints one line per compile plus diagnostics and errors.
   * `VITE_PLUGIN_FABLE_DEBUG` turns it on too, and additionally starts the daemon's log viewer.
   */
  debug?: boolean;
  /**
   * Report diagnostics for files under `fable_modules`.
   *
   * Off by default: those files are the sources Fable restored for the packages the project
   * depends on, so a diagnostic there is about code nobody in this project wrote or can edit.
   *
   * Turning it on is a debugging aid, for when a package itself is what looks broken. Note that
   * with it off, an *error* in a package's sources is dropped too, so `vite build` reports nothing
   * and exits 0 even though Fable did not emit working JavaScript for that file.
   */
  fableModulesDiagnostics?: boolean;
  /**
   * The MSBuild configuration to compile with.
   *
   * Defaults to `Release` for `vite build` and `Debug` for `vite dev`, whatever `--mode` says:
   * a custom mode name is still a build.
   */
  configuration?: FableConfiguration;
}

/** {@link PluginOptions} once the defaults have been applied, so nothing is optional. */
export type ResolvedPluginOptions = Required<Omit<PluginOptions, "fsproj" | "configuration">> &
  Pick<PluginOptions, "fsproj" | "configuration">;

/** Everything the plugin instance carries between hook invocations. */
export interface PluginState {
  /** The user options merged over the plugin defaults. */
  config: ResolvedPluginOptions;
  /** Vite's logger once `configResolved` ran, a console-backed stand-in before that. */
  logger: Logger;
  /** The running daemon, or `null` before `buildStart` and after `buildEnd`. */
  daemon: FableDaemon | null;
  /** Compiled JavaScript per normalized F# source path, served from the `transform` hook. */
  compilableFiles: Map<string, string>;
  /** Every normalized source file in the project, including signature files. */
  sourceFiles: Set<string>;
  /** The entry project file. */
  fsproj: string | null;
  /** The MSBuild configuration to compile with. */
  configuration: FableConfiguration;
  /** MSBuild files that trigger a full re-crack when changed. */
  dependentFiles: Set<string>;
  /** Whether Vite was invoked with `build` rather than `serve`. */
  isBuild: boolean;
  /** Vite's resolved root, so logged paths can be relative to it the way Vite's own are. */
  root: string;
}

/** What one coalesced batch of file changes produced. */
export interface BatchResult {
  /** Diagnostics from the compile, empty when the project was re-cracked instead. */
  diagnostics: Diagnostic[];
  /** Source files whose compiled output this batch replaced. */
  changedFiles: string[];
  /** Whether the batch re-cracked the project rather than compiling files. */
  projectChanged: boolean;
}

/** The result of a `fable/project-changed` request. */
export interface ProjectFileData {
  /** Every source file in the project, in compilation order. */
  sourceFiles: string[];
  /** Diagnostics produced while type-checking. */
  diagnostics: Diagnostic[];
  /** MSBuild files to watch for a re-crack. */
  dependentFiles: string[];
}

/** What the plugin needs from a logger; kept minimal so tests can pass a stub. */
export interface DaemonLogger {
  info(message: string): void;
  error(message: string): void;
}

/** The payload of a `fable/project-changed` request. */
export interface ProjectRequest {
  /** The MSBuild configuration to compile with. */
  configuration: FableConfiguration;
  /** Absolute path of the entry `.fsproj`. */
  project: string;
  /** Directory of the `@fable-org/fable-library-js` package. */
  fableLibrary: string;
  /** Passed through to Fable.Compiler. */
  exclude: string[];
  /** Passed through to Fable.Compiler. */
  noReflection: boolean;
}

/** The result of compiling a set of changed F# files. */
export interface CompileResult {
  /** Compiled JavaScript per source file path, as the daemon reported it. */
  compiledFiles: Record<string, string>;
  /** Diagnostics produced while compiling. */
  diagnostics: Diagnostic[];
}

/**
 * A running Fable daemon.
 *
 * Implementations own the underlying process for their whole lifetime; nothing about how the
 * daemon is spawned or how its JSON-RPC wire format is shaped is visible here. Every method
 * rejects if the daemon is gone rather than awaiting a reply that will never arrive.
 */
export interface FableDaemon {
  /** Cracks and type-checks the project. Nothing is compiled yet. */
  projectChanged(request: ProjectRequest): Promise<ProjectFileData>;
  /** Compiles the whole project, returning compiled JavaScript per source file path. */
  initialCompile(): Promise<Record<string, string>>;
  /** Recompiles the given files and whatever depends on them. */
  compile(files: string[]): Promise<CompileResult>;
  /** Stops the daemon. Safe to call more than once. */
  dispose(): void;
}
