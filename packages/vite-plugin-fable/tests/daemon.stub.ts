import type {
  CompileResult,
  Diagnostic,
  FableDaemon,
  ProjectFileData,
  ProjectRequest,
} from "../src/types.js";

export interface StubDaemonOptions {
  /** Files the daemon reports as belonging to the project. */
  sourceFiles?: string[];
  /** MSBuild files the daemon reports as worth watching. */
  dependentFiles?: string[];
  /** Diagnostics returned from `projectChanged`. */
  projectDiagnostics?: Diagnostic[];
  /** Compiled output keyed by source path, returned from `initialCompile`. */
  compiled?: Record<string, string>;
}

/**
 * A {@link FableDaemon} that answers from canned data instead of running Fable.
 *
 * Every method records its calls and can be made to fail or to resolve on the test's schedule,
 * which is what makes the plugin's batching observable without real compiles in the way.
 */
export interface StubDaemon extends FableDaemon {
  /** One entry per `projectChanged` call. */
  readonly projectChangedCalls: ProjectRequest[];
  /** One entry per `initialCompile` call. */
  readonly initialCompileCalls: number;
  /** One entry per `compile` call, holding the files it was asked for. */
  readonly compileCalls: string[][];
  /** How many times `dispose` ran. */
  readonly disposeCalls: number;
  /** Result of the next `compile`, keyed by source path. */
  setCompileResult(compiled: Record<string, string>, diagnostics?: Diagnostic[]): void;
  /** Make every subsequent call reject, as a dead daemon would. */
  failWith(error: Error): void;
  /**
   * Hold `compile` open until the returned function is called, so a test can decide exactly when a
   * compile finishes relative to other events.
   */
  blockNextCompile(): () => void;
}

export function createStubDaemon(options: StubDaemonOptions = {}): StubDaemon {
  const projectChangedCalls: ProjectRequest[] = [];
  const compileCalls: string[][] = [];
  let initialCompileCalls = 0;
  let disposeCalls = 0;
  let failure: Error | null = null;
  let blocked: Promise<void> | null = null;
  let compiled: Record<string, string> = options.compiled ?? {};
  let compileDiagnostics: Diagnostic[] = [];

  function guard(): void {
    if (failure) throw failure;
  }

  const stub: StubDaemon = {
    get projectChangedCalls() {
      return projectChangedCalls;
    },
    get initialCompileCalls() {
      return initialCompileCalls;
    },
    get compileCalls() {
      return compileCalls;
    },
    get disposeCalls() {
      return disposeCalls;
    },

    setCompileResult(next: Record<string, string>, diagnostics: Diagnostic[] = []): void {
      compiled = next;
      compileDiagnostics = diagnostics;
    },

    failWith(error: Error): void {
      failure = error;
    },

    blockNextCompile(): () => void {
      let resolve!: () => void;
      blocked = new Promise<void>((r: () => void) => {
        resolve = r;
      });
      return resolve;
    },

    async projectChanged(request: ProjectRequest): Promise<ProjectFileData> {
      guard();
      projectChangedCalls.push(request);
      return {
        sourceFiles: options.sourceFiles ?? [],
        diagnostics: options.projectDiagnostics ?? [],
        dependentFiles: options.dependentFiles ?? [],
      };
    },

    async initialCompile(): Promise<Record<string, string>> {
      guard();
      initialCompileCalls += 1;
      return compiled;
    },

    async compile(files: string[]): Promise<CompileResult> {
      guard();
      compileCalls.push(files);
      if (blocked) {
        const gate: Promise<void> = blocked;
        blocked = null;
        await gate;
      }
      const compiledFiles: Record<string, string> = {};
      for (const file of files) {
        if (compiled[file] !== undefined) compiledFiles[file] = compiled[file];
      }
      return { compiledFiles, diagnostics: compileDiagnostics };
    },

    dispose(): void {
      disposeCalls += 1;
    },
  };

  return stub;
}
