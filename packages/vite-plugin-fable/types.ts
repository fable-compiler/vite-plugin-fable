import type { Logger } from "vite";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import type { Subscription } from "rxjs";
import type { JSONRPCEndpoint } from "ts-lsp-client";

/**
 * A generic F# discriminated union case with its associated fields, as it arrives over JSON-RPC.
 *
 * The wire format is positional: `fields` mirrors the declaration order of the F# case, so the
 * call sites index into it by number. See roadmap item 9.
 */
export interface FSharpDiscriminatedUnion {
  /** The name of the case, mirroring the F# case name. */
  case: string;
  /** The fields associated with the case, in declaration order. */
  fields: any[];
}

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

/** Options for configuring the plugin. */
export interface PluginOptions {
  /** The main fsproj to load. Defaults to the single `.fsproj` found next to the Vite config. */
  fsproj?: string;
  /**
   * Apply a JSX transformation after Fable compilation.
   *
   * `transform` uses the classic runtime, `automatic` the automatic runtime.
   *
   * @see https://oxc.rs/docs/guide/usage/transformer/jsx
   */
  jsx?: "transform" | "preserve" | "automatic" | null;
  /** Pass `noReflection` to Fable.Compiler. */
  noReflection?: boolean;
  /** Pass `exclude` to Fable.Compiler. */
  exclude?: string[];
}

/** Everything the plugin instance carries between hook invocations. */
export interface PluginState {
  /** The user options merged over the plugin defaults. */
  config: PluginOptions;
  /** Vite's logger once `configResolved` ran, a console-backed stand-in before that. */
  logger: Logger;
  /** The spawned `Fable.Daemon` process, or `null` before `buildStart`. */
  dotnetProcess: ChildProcessWithoutNullStreams | null;
  /** JSON-RPC endpoint talking to {@link PluginState.dotnetProcess} over stdio. */
  endpoint: JSONRPCEndpoint | null;
  /** Compiled JavaScript per normalized F# source path, served from the `transform` hook. */
  compilableFiles: Map<string, string>;
  /** Every normalized source file in the project, including signature files. */
  sourceFiles: Set<string>;
  /** The entry project file. */
  fsproj: string | null;
  /** The MSBuild configuration to compile with, `Debug` or `Release`. */
  configuration: string;
  /** MSBuild files that trigger a full re-crack when changed. */
  dependentFiles: Set<string>;
  /** Subscription draining the pending-change queue during dev. */
  pendingChanges: Subscription | null;
  /** Shared between concurrent hot updates so they await a single compile. */
  hotPromiseWithResolvers: PromiseWithResolvers<Diagnostic[]> | null;
  /** Whether Vite was invoked with `build` rather than `serve`. */
  isBuild: boolean;
}

/** An F# implementation or signature file changed. */
export interface FSharpFileChanged {
  type: "FSharpFileChanged";
  /** The F# file that changed. */
  file: string;
}

/** A project file or one of its MSBuild dependencies changed. */
export interface ProjectFileChanged {
  type: "ProjectFileChanged";
  /** The project file that changed. */
  file: string;
}

/** The events the Vite hooks push onto the pending-change queue. */
export type HookEvent = FSharpFileChanged | ProjectFileChanged;

/** One buffered window of {@link HookEvent}s, reduced into the work it implies. */
export interface PendingChangesState {
  /** Whether a full re-crack is needed. */
  projectChanged: boolean;
  /** The F# files to recompile. */
  fsharpFiles: Set<string>;
  /** The project files that triggered {@link PendingChangesState.projectChanged}. */
  projectFiles: Set<string>;
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
