import { describe, expect, test } from "bun:test";
import { daemonEnvironment } from "../src/daemon.js";

describe("daemonEnvironment", () => {
  test("leaves the GC alone by default", () => {
    const env: NodeJS.ProcessEnv = daemonEnvironment({ debug: false, serverGC: false }, {});
    expect(env.DOTNET_gcServer).toBeUndefined();
    expect(env.DOTNET_GCConserveMemory).toBeUndefined();
  });

  // The daemon itself runs with it. It clears both variables once started, so the builds it spawns
  // do not inherit them either way.
  test("passes on a GC setting the user exported themselves", () => {
    const env: NodeJS.ProcessEnv = daemonEnvironment(
      { debug: false, serverGC: false },
      { DOTNET_gcServer: "1" },
    );
    expect(env.DOTNET_gcServer).toBe("1");
  });

  test("turns on server GC, and conserves memory with it", () => {
    const env: NodeJS.ProcessEnv = daemonEnvironment({ debug: false, serverGC: true }, {});
    expect(env.DOTNET_gcServer).toBe("1");
    expect(env.DOTNET_GCConserveMemory).toBe("7");
  });

  test("passes the debug option as the variable the daemon reads", () => {
    expect(daemonEnvironment({ debug: true, serverGC: false }, {}).VITE_PLUGIN_FABLE_DEBUG).toBe(
      "1",
    );
    // Cleared rather than inherited: the option decides, not a variable left in the shell.
    expect(
      daemonEnvironment({ debug: false, serverGC: false }, { VITE_PLUGIN_FABLE_DEBUG: "1" })
        .VITE_PLUGIN_FABLE_DEBUG,
    ).toBe("");
  });
});
