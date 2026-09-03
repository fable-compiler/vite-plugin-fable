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
  /** Diagnostics returned from `initialCompile`, which is where Fable's own logs arrive. */
  initialCompileDiagnostics?: Diagnostic[];
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
  /**
   * What the next `compile` returns, keyed by source path. The real daemon answers with every file
   * it recompiled, which includes files downstream of the one that changed — not just the files it
   * was asked about.
   */
  setCompileResult(compiled: Record<string, string>, diagnostics?: Diagnostic[]): void;
  /** Make every subsequent call reject, as a dead daemon would. */
  failWith(error: Error): void;
  /**
   * Hold `compile` open until the returned function is called, so a test can decide exactly when a
   * compile finishes relative to other events.
   */
  blockNextCompile(): () => void;
  /**
   * Hold `projectChanged` open until the returned function is called, so a test can watch what the
   * plugin does while the first crack is still running.
   */
  blockNextProjectChange(): () => void;
}

export function createStubDaemon(options: StubDaemonOptions = {}): StubDaemon {
  const projectChangedCalls: ProjectRequest[] = [];
  const compileCalls: string[][] = [];
  let initialCompileCalls = 0;
  let disposeCalls = 0;
  let failure: Error | null = null;
  let blocked: Promise<void> | null = null;
  let blockedProjectChange: Promise<void> | null = null;
  const compiled: Record<string, string> = options.compiled ?? {};
  let nextCompile: Record<string, string> | null = null;
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
      nextCompile = next;
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

    blockNextProjectChange(): () => void {
      let resolve!: () => void;
      blockedProjectChange = new Promise<void>((r: () => void) => {
        resolve = r;
      });
      return resolve;
    },

    async projectChanged(request: ProjectRequest): Promise<ProjectFileData> {
      guard();
      projectChangedCalls.push(request);

      if (blockedProjectChange) {
        const gate: Promise<void> = blockedProjectChange;
        blockedProjectChange = null;
        await gate;
      }
      return {
        sourceFiles: options.sourceFiles ?? [],
        diagnostics: options.projectDiagnostics ?? [],
        dependentFiles: options.dependentFiles ?? [],
      };
    },

    async initialCompile(): Promise<CompileResult> {
      guard();
      initialCompileCalls += 1;
      return {
        compiledFiles: compiled,
        diagnostics: options.initialCompileDiagnostics ?? [],
      };
    },

    async compile(files: string[]): Promise<CompileResult> {
      guard();
      compileCalls.push(files);

      // Decide the answer when the call arrives, not when it resolves: a blocked compile must not
      // pick up a result queued for the compile that came after it.
      let compiledFiles: Record<string, string>;
      if (nextCompile) {
        compiledFiles = nextCompile;
        nextCompile = null;
      } else {
        compiledFiles = {};
        for (const file of files) {
          if (compiled[file] !== undefined) compiledFiles[file] = compiled[file];
        }
      }
      const diagnostics: Diagnostic[] = compileDiagnostics;

      if (blocked) {
        const gate: Promise<void> = blocked;
        blocked = null;
        await gate;
      }
      return { compiledFiles, diagnostics };
    },

    dispose(): void {
      disposeCalls += 1;
    },
  };

  return stub;
}
