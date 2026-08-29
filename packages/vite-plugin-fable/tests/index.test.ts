import { describe, expect, test } from "bun:test";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Logger, Plugin, ResolvedConfig } from "vite";
import { createFablePlugin } from "../src/index.js";
import { createStubDaemon, type StubDaemon, type StubDaemonOptions } from "./daemon.stub.js";
import type { Diagnostic, PluginOptions, ProjectRequest } from "../src/types.js";

const sampleProject: string = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../../sample-project",
);
const mathFs = `${sampleProject}/Math.fs`;
const libraryFs = `${sampleProject}/Library.fs`;
const appFsproj = `${sampleProject}/App.fsproj`;

/** A logger that swallows output, so the test runner stays readable. */
const silentLogger = {
  info: () => {},
  warn: () => {},
  error: () => {},
  warnOnce: () => {},
  clearScreen: () => {},
  hasErrorLogged: () => false,
  hasWarned: false,
} as unknown as Logger;

function resolvedConfig(overrides: Partial<ResolvedConfig> = {}): ResolvedConfig {
  return {
    logger: silentLogger,
    env: { MODE: "development" },
    command: "serve",
    configFile: `${sampleProject}/vite.config.js`,
    ...overrides,
  } as unknown as ResolvedConfig;
}

/** The slice of Vite's plugin context the hooks actually reach for. */
interface PluginContextStub {
  addWatchFile(id: string): void;
  /** Rollup's `this.error` throws, and the plugin relies on that to abort a build. */
  error(message: string): never;
}

/** The shape `handleHotUpdate` receives for each affected module. */
interface HotModule {
  id: string;
  importers: Set<unknown>;
}

/** Enough of a dev server for the hot-update hook. */
interface HotServerStub {
  hot: { send(payload?: unknown): unknown };
}

type TransformOutput = { code: string; map: null } | undefined;

interface Harness {
  plugin: Plugin;
  daemon: StubDaemon;
  watched: string[];
  /** Runs `configResolved` then `buildStart`, as Vite would. */
  start(config?: ResolvedConfig): Promise<void>;
  /** Calls the `transform` hook the way Vite's plugin container does. */
  transform(id: string): Promise<TransformOutput>;
}

function harness(pluginOptions: PluginOptions = {}, stub: StubDaemonOptions = {}): Harness {
  const daemon: StubDaemon = createStubDaemon(stub);
  const watched: string[] = [];
  const plugin: Plugin = createFablePlugin(
    { fsproj: appFsproj, ...pluginOptions },
    (): StubDaemon => daemon,
  );

  const context: PluginContextStub = {
    addWatchFile: (id: string): void => {
      watched.push(id);
    },
    error: (message: string): never => {
      throw new Error(message);
    },
  };

  return {
    plugin,
    daemon,
    watched,
    async start(config = resolvedConfig()) {
      await (plugin.configResolved as any).call(context, config);
      await (plugin.buildStart as any).call(context, {});
    },
    async transform(id: string): Promise<TransformOutput> {
      return (plugin.transform as any).handler.call(context, "", id);
    },
  };
}

/** A config for `vite build`, where failures should be fatal. */
function buildConfig(): ResolvedConfig {
  return resolvedConfig({ env: { MODE: "production" } as any, command: "build" as const });
}

/** An error-severity diagnostic pointing at `file`. */
function errorAt(file: string): Diagnostic {
  return {
    errorNumberText: "FS0001",
    message: "This expression was expected to have type int",
    range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 5 },
    severity: "Error",
    fileName: file,
  };
}

/** Waits past the plugin's 50ms coalescing window. */
function afterCoalescing(): Promise<void> {
  return new Promise((resolve: () => void) => setTimeout(resolve, 120));
}

describe("configResolved", () => {
  test("uses the fsproj given in the plugin options", async () => {
    const h: Harness = harness();
    await (h.plugin.configResolved as any).call({}, resolvedConfig());
    await (h.plugin.buildStart as any).call({ addWatchFile: () => {} }, {});
    expect(h.daemon.projectChangedCalls[0].project).toBe(appFsproj);
  });

  test("compiles Release for a production build and Debug otherwise", async () => {
    const release: Harness = harness();
    await release.start(
      resolvedConfig({ env: { MODE: "production" } as any, command: "build" as const }),
    );
    expect(release.daemon.projectChangedCalls[0].configuration).toBe("Release");

    const debug: Harness = harness();
    await debug.start();
    expect(debug.daemon.projectChangedCalls[0].configuration).toBe("Debug");
  });

  test("passes the Fable options through to the daemon", async () => {
    const h: Harness = harness({ noReflection: true, exclude: ["Foo.Bar"] });
    await h.start();
    const request: ProjectRequest = h.daemon.projectChangedCalls[0];
    expect(request.noReflection).toBe(true);
    expect(request.exclude).toEqual(["Foo.Bar"]);
  });
});

describe("buildStart", () => {
  test("cracks and compiles the project once", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const x = 1;" } },
    );
    await h.start();
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
    expect(h.daemon.initialCompileCalls).toBe(1);
  });

  test("watches source files and MSBuild dependencies", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();
    expect(h.watched).toContain(mathFs);
    expect(h.watched).toContain(appFsproj);
  });

  test("fails the build when the daemon is unavailable", async () => {
    const h: Harness = harness();
    h.daemon.failWith(new Error("Could not spawn `dotnet`"));
    expect(h.start(buildConfig())).rejects.toThrow(/Could not spawn/);
  });

  test("fails the build on an F# error diagnostic", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(mathFs)] },
    );
    expect(h.start(buildConfig())).rejects.toThrow(/FS0001/);
  });

  test("serves in dev despite an F# error, so the overlay can show it", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], projectDiagnostics: [errorAt(mathFs)] },
    );
    await h.start();
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
  });

  test("keeps building when the project only produces warnings", async () => {
    const warning: Diagnostic = { ...errorAt(mathFs), severity: "Warning" };
    const h: Harness = harness({}, { sourceFiles: [mathFs], projectDiagnostics: [warning] });
    await h.start(buildConfig());
    expect(h.daemon.initialCompileCalls).toBe(1);
  });
});

describe("transform", () => {
  test("serves the compiled output for a project file", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const sum = 1;" } },
    );
    await h.start();
    const result: TransformOutput = await h.transform(mathFs);
    expect(result?.code).toBe("export const sum = 1;");
  });

  test("returns nothing for an F# file the daemon never compiled", async () => {
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start();
    expect(await h.transform(`${sampleProject}/Unknown.fs`)).toBeUndefined();
  });

  test("errors rather than handing raw F# to the JS parser during a build", async () => {
    const h: Harness = harness({}, { sourceFiles: [] });
    await h.start(buildConfig());
    expect(h.transform(`${sampleProject}/Unknown.fs`)).rejects.toThrow(
      `${sampleProject}/Unknown.fs was not compiled by Fable, so it cannot be bundled.`,
    );
  });

  test("applies the JSX transform when jsx is automatic", async () => {
    const h: Harness = harness(
      { jsx: "automatic" },
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = <div>hi</div>;" } },
    );
    await h.start();
    const result: TransformOutput = await h.transform(libraryFs);
    expect(result?.code).toContain("jsx");
    expect(result?.code).not.toContain("<div>");
  });

  test("leaves JSX alone when the option is off", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [libraryFs], compiled: { [libraryFs]: "export const a = 1;" } },
    );
    await h.start();
    expect((await h.transform(libraryFs))?.code).toBe("export const a = 1;");
  });
});

describe("hot updates", () => {
  test("recompiles a changed F# file and refreshes its output", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const v = 1;" } },
    );
    await h.start();

    h.daemon.setCompileResult({ [mathFs]: "export const v = 2;" });
    const modules: HotModule[] = [{ id: mathFs, importers: new Set([{}]) }];
    await (h.plugin.handleHotUpdate as any).call(
      {},
      { file: mathFs, server: { hot: { send: () => {} } }, modules },
    );

    expect(h.daemon.compileCalls).toEqual([[mathFs]]);
    expect((await h.transform(mathFs))?.code).toBe("export const v = 2;");
  });

  test("ignores files that are not part of the project", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const v = 1;" } },
    );
    await h.start();
    await (h.plugin.handleHotUpdate as any).call(
      {},
      { file: `${sampleProject}/README.md`, server: { hot: { send: () => {} } }, modules: [] },
    );
    expect(h.daemon.compileCalls).toHaveLength(0);
  });

  test("sends an overlay error and no update when compilation fails", async () => {
    const h: Harness = harness(
      {},
      { sourceFiles: [mathFs], compiled: { [mathFs]: "export const v = 1;" } },
    );
    await h.start();

    const diagnostic: Diagnostic = {
      errorNumberText: "FS0001",
      message: "This expression was expected to have type int",
      range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 5 },
      severity: "Error",
      fileName: mathFs,
    };
    h.daemon.setCompileResult({}, [diagnostic]);

    const sent: any[] = [];
    const result: unknown = await (h.plugin.handleHotUpdate as any).call(
      {},
      {
        file: mathFs,
        server: { hot: { send: (payload: any): number => sent.push(payload) } },
        modules: [{ id: mathFs, importers: new Set([{}]) }],
      },
    );

    expect(sent[0].type).toBe("error");
    expect(sent[0].err.message).toBe(diagnostic.message);
    expect(result).toEqual([]);
  });

  test("coalesces concurrent changes into one compile", async () => {
    const h: Harness = harness(
      {},
      {
        sourceFiles: [mathFs, libraryFs],
        compiled: { [mathFs]: "export const v = 1;", [libraryFs]: "export const w = 1;" },
      },
    );
    await h.start();

    const server: HotServerStub = { hot: { send: (): void => {} } };
    await Promise.all([
      (h.plugin.handleHotUpdate as any).call(
        {},
        { file: mathFs, server, modules: [{ id: mathFs, importers: new Set([{}]) }] },
      ),
      (h.plugin.handleHotUpdate as any).call(
        {},
        { file: libraryFs, server, modules: [{ id: libraryFs, importers: new Set([{}]) }] },
      ),
    ]);

    expect(h.daemon.compileCalls).toHaveLength(1);
    expect(h.daemon.compileCalls[0].sort()).toEqual([libraryFs, mathFs].sort());
  });
});

describe("watchChange", () => {
  test("re-cracks the project when an MSBuild dependency changes", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();
    await (h.plugin.watchChange as any).call({}, appFsproj, { event: "update" });
    await afterCoalescing();
    expect(h.daemon.projectChangedCalls).toHaveLength(2);
  });

  test("ignores changes to files it does not track", async () => {
    const h: Harness = harness({}, { sourceFiles: [mathFs], dependentFiles: [appFsproj] });
    await h.start();
    await (h.plugin.watchChange as any).call({}, `${sampleProject}/README.md`, { event: "update" });
    await afterCoalescing();
    expect(h.daemon.projectChangedCalls).toHaveLength(1);
  });
});

describe("buildEnd", () => {
  test("disposes the daemon", async () => {
    const h: Harness = harness();
    await h.start();
    (h.plugin.buildEnd as any).call({});
    expect(h.daemon.disposeCalls).toBe(1);
  });
});
