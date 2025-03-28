# Fable.Daemon

This project uses JSON-RPC (JSON Remote Procedure Call) for communication between the main vite plugin interface [index.js](../index.js) and the [F# daemon](./Program.fs).

The architecture enables efficient bi-directional communication, allowing features like hot reloading and incremental compilation of `.fs` source files and `.fsproj`.

---

## JRPC Server (.NET)

The F# daemon sets up a JSON-RPC server using the `StreamJsonRpc` library. The server listens for incoming RPC calls and processes them.

## Methods

### 1. `fable/project-changed`
- **Purpose**: Handles project configuration and changes via [Project Cracking](./CoolCatCracking.fs) to analyze and **extract metadata** from `.fsproj` files.
- **Input**: Project configuration payload.
- **Output**: Source files, diagnostics, and dependent files.

### 2. `fable/initial-compile`
- **Purpose**: Performs the initial compilation of the entire project.
- **Input**: None.
- **Output**: Compiled F# files as JavaScript.

### 3. `fable/compile`
- **Purpose**: Handles incremental compilation of changed files and HMR.
- **Input**: Changed files payload.
- **Output**: Compiled JavaScript for the changed F# files.


#### Communication Streams
The daemon communicates over standard input/output streams:

```fsharp
let daemon = new FableServer(
    Console.OpenStandardOutput(),
    Console.OpenStandardInput(),
    logger
)
```

## JRPC Client (JavaScript)

The [Vite plugin (index.js)](../index.js) acts as the JSON-RPC client, making calls to the F# daemon using the exposed endpoints/methods.

### Example RPC Call

```js
const result = await state.endpoint.send("fable/project-changed", {
    configuration: state.configuration,
    project: state.fsproj,
    fableLibrary,
    exclude: state.config.exclude,
    noReflection: state.config.noReflection
});
```

## Architecture Benefits

- **Incremental Compilation**: The `fable/compile` method enables incremental compilation, allowing for fast updates during development.
- **Efficiency**: Communication over standard input/output streams ensures low overhead.
- **Extensibility**: The JSON-RPC architecture allows for easy addition of new methods and features.


