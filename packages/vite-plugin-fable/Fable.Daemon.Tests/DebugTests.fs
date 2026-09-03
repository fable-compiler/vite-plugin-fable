module Fable.Daemon.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open Nerdbank.Streams
open StreamJsonRpc
open Fable.Daemon

type Path with
    static member CombineNormalize ([<ParamArray>] parts : string array) = Path.Combine parts |> Path.GetFullPath

let fableLibrary =
    Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../node_modules/@fable-org/fable-library-js")

let sampleApp =
    {
        Project = Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../sample-project/App.fsproj")
        FableLibrary = fableLibrary
        Configuration = "Release"
        Exclude = Array.empty
        NoReflection = false
    }

let telplin =
    {
        Project = Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../../telplin/tool/client/OnlineTool.fsproj")
        FableLibrary = fableLibrary
        Configuration = "Debug"
        Exclude = Array.empty
        NoReflection = false
    }

let fantomasTools =
    {
        Project =
            Path.CombineNormalize (
                __SOURCE_DIRECTORY__,
                "../../../../fantomas-tools/src/client/fsharp/FantomasTools.fsproj"
            )
        FableLibrary = fableLibrary
        Configuration = "Debug"
        Exclude = Array.empty
        NoReflection = false
    }

// let ronnies =
//     {
//         Project = @"C:\Users\nojaf\Projects\ronnies.be\app\App.fsproj"
//         FableLibrary = fableLibrary
//         Configuration = "Debug"
//         Exclude = [| "Nojaf.Fable.React.Plugin" |]
//         NoReflection = true
//     }

[<Test>]
let DebugTest () =
    task {
        let config = sampleApp
        Directory.SetCurrentDirectory (FileInfo(config.Project).DirectoryName)

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let! typecheckResponse = daemon.ProjectChanged config
        ignore typecheckResponse

        let! compileFiles =
            daemon.CompileFiles
                {
                    FileNames =
                        [|
                            Path.CombineNormalize (FileInfo(sampleApp.Project).Directory.FullName, "Math.fs")
                        |]
                }

        printfn "response: %A" compileFiles
        client.Dispose ()
        (daemon :> IDisposable).Dispose()

        Assert.Pass ()
    }

/// Editing a signature file has to reach the implementation it describes: the daemon maps
/// `Foo.fsi` to `Foo.fs` before walking dependents, so a broken signature must surface as a
/// diagnostic on the implementation rather than silently compiling stale output.
[<Test>]
let ``changing a signature file reports a diagnostic on the implementation`` () =
    task {
        let config = sampleApp
        let projectDir = FileInfo(config.Project).Directory.FullName
        Directory.SetCurrentDirectory projectDir

        let signatureFile = Path.CombineNormalize (projectDir, "Component.fsi")
        let originalSignature = File.ReadAllText signatureFile

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        try
            let! _ = daemon.ProjectChanged config

            // Drop the `Component` val the implementation exports.
            File.WriteAllText (signatureFile, "module Components.Component\n\nopen Fable.Core\n")

            let! compileResponse = daemon.CompileFiles { FileNames = [| signatureFile |] }

            match compileResponse with
            | FileChangedResult.Success (_, diagnostics) ->
                let errors =
                    diagnostics |> Array.filter (fun d -> d.Severity.ToLowerInvariant () = "error")

                Assert.That (errors, Is.Not.Empty, "expected the broken signature to produce an error")
            | other -> Assert.Fail $"expected a successful compile response, got %A{other}"
        finally
            File.WriteAllText (signatureFile, originalSignature)
            client.Dispose ()
            (daemon :> IDisposable).Dispose()
    }

/// MSBuild reports its own failures on stdout and leaves stderr empty, so the exit code is the
/// only signal worth failing on, and a message built from stderr alone names no reason at all.
[<Test>]
let ``a failing dotnet msbuild call reports what MSBuild said`` () =
    task {
        let fsproj =
            FileInfo (Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../sample-project/App.fsproj"))

        let run =
            MSBuild.dotnet_msbuild NullLogger.Instance fsproj "-t:NoSuchTarget"
            |> Async.StartAsTask

        // Also that it comes back at all: draining one pipe to the end before touching the other
        // can deadlock, and nothing on the way back to the plugin times out.
        let! finished = Task.WhenAny (run :> Task, Task.Delay (TimeSpan.FromMinutes 2.))
        Assert.That (finished, Is.SameAs (run :> Task), "dotnet msbuild never came back")

        let error = Assert.Throws<AggregateException>(fun () -> run.Wait ())

        Assert.That (
            error.InnerException.Message,
            Does.Contain "MSB4057",
            "the failure did not say what MSBuild complained about"
        )
    }

/// Since MSBuild 16.9 an import no longer adds itself to `MSBuildAllProjects`, so asking for that
/// property alone reports the fsproj and a handful of SDK targets and misses the file people
/// actually edit. A `Directory.Build.props` that is not a dependent file is a design time build
/// cache that survives an edit to it, and a file the plugin never watches.
[<Test>]
let ``the project's Directory.Build.props is reported as a file to watch`` () =
    task {
        let config = sampleApp
        let projectDir = FileInfo(config.Project).Directory.FullName
        Directory.SetCurrentDirectory projectDir

        let directoryBuildProps =
            Path.CombineNormalize (projectDir, "Directory.Build.props")

        Assert.That (
            File.Exists directoryBuildProps,
            Is.True,
            "the sample project no longer has a Directory.Build.props, so this test proves nothing"
        )

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        try
            let! response = daemon.ProjectChanged config

            match response with
            | ProjectChangedResult.Error error -> Assert.Fail $"expected the project to crack, got {error}"
            | ProjectChangedResult.Success (_, _, dependentFiles) ->

            Assert.That (
                dependentFiles |> Array.map Path.GetFullPath,
                Does.Contain directoryBuildProps,
                $"""Directory.Build.props was not reported: %s{String.concat ", " dependentFiles}"""
            )
        finally
            client.Dispose ()
            (daemon :> IDisposable).Dispose()
    }

/// The resolver forgets its cache keys at the start of every crack, so the next one asks MSBuild
/// again which files its evaluation depends on. The risk in forgetting them is that nothing fills
/// them back in: `MSBuildProjectFiles` would then answer with an empty list, the plugin would watch
/// no MSBuild inputs at all, and editing the fsproj would stop re-cracking the project.
[<Test>]
let ``cracking twice still reports the MSBuild files to watch`` () =
    task {
        let config = sampleApp
        Directory.SetCurrentDirectory (FileInfo(config.Project).DirectoryName)

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let dependentFiles (response : ProjectChangedResult) =
            match response with
            | ProjectChangedResult.Success (_, _, dependentFiles) -> dependentFiles
            | ProjectChangedResult.Error error -> failwith $"expected the project to crack, got {error}"

        try
            let! first = daemon.ProjectChanged config
            let! second = daemon.ProjectChanged config

            for response in [ first ; second ] do
                let files = dependentFiles response

                Assert.That (files, Is.Not.Empty, "no MSBuild files were reported to watch")

                Assert.That (
                    files
                    |> Array.exists (fun f -> f.EndsWith ("App.fsproj", StringComparison.Ordinal)),
                    Is.True,
                    $"""the project file itself was not reported: %s{String.concat ", " files}"""
                )
        finally
            client.Dispose ()
            (daemon :> IDisposable).Dispose()
    }

/// The daemon keeps Fable's `File` values between compiles so an unchanged file is not read and
/// hashed again, and forgets the ones the plugin reports as changed. A change it failed to forget
/// would be compiled from what the file used to say, and nothing else would notice: no error, just
/// yesterday's JavaScript. Two edits in a row, because the first is served by a cache that was
/// filled by the crack and the second by one the compile before it filled.
[<Test>]
let ``an edited file is compiled from what it now says`` () =
    task {
        let config = sampleApp
        let projectDir = FileInfo(config.Project).Directory.FullName
        Directory.SetCurrentDirectory projectDir

        let mathFile = Path.CombineNormalize (projectDir, "Math.fs")
        // Bytes rather than text: `Math.fs` opens with a BOM, and reading it as a string drops it,
        // so restoring it as a string would leave the file changed in git.
        let originalMath = File.ReadAllBytes mathFile

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        let compileMath (literal : int) =
            task {
                File.WriteAllText (mathFile, $"module Math\n\nlet sum a b = a + %i{literal}\n")
                let! response = daemon.CompileFiles { FileNames = [| mathFile |] }

                match response with
                | FileChangedResult.Success (compiled, _) ->
                    return
                        compiled
                        |> Map.tryPick (fun key javaScript ->
                            if key.EndsWith ("Math.fs", StringComparison.Ordinal) then
                                Some javaScript
                            else
                                None
                        )
                | other -> return failwith $"expected a successful compile response, got %A{other}"
            }

        try
            let! _ = daemon.ProjectChanged config

            let! first = compileMath 41
            Assert.That (first, Is.Not.Null, "the daemon compiled no Math.fs")
            Assert.That (first.Value, Does.Contain "41")

            let! second = compileMath 42
            Assert.That (second.Value, Does.Contain "42")
            Assert.That (second.Value, Does.Not.Contain "41")
        finally
            File.WriteAllBytes (mathFile, originalMath)
            client.Dispose ()
            (daemon :> IDisposable).Dispose()
    }

/// A request the daemon cannot serve has to come back as an error. `PostAndAsyncReply` has no
/// timeout, so a message loop that dies instead of answering leaves the plugin waiting inside
/// `buildStart` forever: no URL printed, no overlay, nothing on screen. The loop therefore answers
/// every message and carries on, whatever the message did to it.
[<Test>]
let ``a request that cannot be served is answered, and the daemon keeps serving`` () =
    task {
        let config = sampleApp
        Directory.SetCurrentDirectory (FileInfo(config.Project).DirectoryName)

        let struct (serverStream, clientStream) = FullDuplexStream.CreatePair ()

        let daemon =
            new Program.FableServer (serverStream, serverStream, NullLogger.Instance)

        let client = new JsonRpc (clientStream, clientStream)
        client.StartListening ()

        try
            // Nothing has been cracked yet, so there is no project to compile this against.
            let compile =
                daemon.CompileFiles
                    {
                        FileNames =
                            [|
                                Path.CombineNormalize (FileInfo(config.Project).Directory.FullName, "Math.fs")
                            |]
                    }

            let! answered = Task.WhenAny (compile :> Task, Task.Delay (TimeSpan.FromMinutes 1.))
            Assert.That (answered, Is.SameAs (compile :> Task), "the daemon never answered the request")

            match compile.Result with
            | FileChangedResult.Error _ -> ()
            | other -> Assert.Fail $"expected an error response, got %A{other}"

            // The loop is still alive, so a request it can serve is served.
            let! projectResponse = daemon.ProjectChanged config

            match projectResponse with
            | ProjectChangedResult.Success _ -> ()
            | ProjectChangedResult.Error error -> Assert.Fail $"expected the project to crack, got {error}"
        finally
            client.Dispose ()
            (daemon :> IDisposable).Dispose()
    }

/// The design time build cache is keyed on everything that changes what Fable emits. Options that
/// only live in the Vite config used to be left out, so flipping one served stale JavaScript.
module CacheKeyTests =

    open Fable.Compiler.ProjectCracker

    let private emptyResponse : ProjectOptionsResponse =
        {
            ProjectOptions = [| "--define:FABLE_COMPILER" |]
            ProjectReferences = Array.empty
            OutputType = None
            TargetFramework = Some "net10.0"
        }

    /// A cache key pointing at a scratch file, so writing one does not touch a real obj folder.
    let private mkKey (exclude : string list) (noReflection : bool) : Caching.CacheKey =
        let fsproj =
            FileInfo (Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../sample-project/App.fsproj"))

        {
            MainFsproj = fsproj
            CacheFile = FileInfo (Path.Combine (Path.GetTempPath (), "vite-plugin-fable-cache-test.bin"))
            DependentFiles = [ fsproj ]
            Defines = Set.ofList [ "FABLE_COMPILER" ]
            Configuration = "Release"
            Exclude = exclude
            NoReflection = noReflection
            FableCompilerVersion = Caching.fableCompilerVersion
        }

    [<Test>]
    let ``an unchanged key reuses the cache`` () =
        let key = mkKey [] false
        Caching.writeDesignTimeBuild key emptyResponse

        match Caching.canReuseDesignTimeBuildCache key with
        | Ok _ -> Assert.Pass ()
        | Error reason -> Assert.Fail $"expected the cache to be reusable, got %A{reason}"

    [<Test>]
    let ``changing noReflection invalidates the cache`` () =
        Caching.writeDesignTimeBuild (mkKey [] false) emptyResponse

        match Caching.canReuseDesignTimeBuildCache (mkKey [] true) with
        | Error (Caching.InvalidCacheReason.NoReflectionMismatch (false, true)) -> Assert.Pass ()
        | other -> Assert.Fail $"expected a NoReflection mismatch, got %A{other}"

    [<Test>]
    let ``changing exclude invalidates the cache`` () =
        Caching.writeDesignTimeBuild (mkKey [] false) emptyResponse

        match Caching.canReuseDesignTimeBuildCache (mkKey [ "Some.Plugin" ] false) with
        | Error (Caching.InvalidCacheReason.ExcludeMismatch ([], [ "Some.Plugin" ])) -> Assert.Pass ()
        | other -> Assert.Fail $"expected an Exclude mismatch, got %A{other}"

/// The wire format is the contract with `src/daemon.ts`, and the only way the plugin learns
/// anything. These serialise with the daemon's own options rather than a copy, and compare against
/// fixtures the JavaScript tests decode, so renaming or reordering a field fails on both sides
/// instead of silently changing what an index means.
module WireTests =

    open System.Text.Json

    let private fixture (name : string) : string =
        Path.CombineNormalize (__SOURCE_DIRECTORY__, "../tests/fixtures", name)

    /// The repo formatter owns these files and gives them a trailing newline. Neither that nor
    /// the line endings are part of the contract; the keys, their order and their values are.
    let private normalize (json : string) : string = json.Replace("\r\n", "\n").TrimEnd '\n'

    let private serialize (value : 'T) : string =
        let json = JsonSerializer.Serialize<'T>(value, Wire.serializerOptions ())
        // Round-tripped through JsonDocument so the fixture is formatted, not one long line.
        use document = JsonDocument.Parse json
        JsonSerializer.Serialize (document, JsonSerializerOptions (WriteIndented = true))

    let private diagnostic : Diagnostic =
        {
            ErrorNumberText = "FS0025"
            Message = "Incomplete pattern matches on this expression."
            Range =
                {
                    StartLine = 3
                    StartColumn = 4
                    EndLine = 3
                    EndColumn = 9
                }
            Severity = "Warning"
            FileName = "/project/Math.fs"
            Tag = "FSHARP"
        }

    /// What Fable reports about a file that type-checks but that it cannot translate. It carries no
    /// error number, which is why `ErrorNumberText` is empty rather than absent.
    let private fableDiagnostic : Diagnostic =
        {
            ErrorNumberText = ""
            Message = "Microsoft.FSharp.Control.FSharpAsync.RunSynchronously (static) is not supported by Fable"
            Range =
                {
                    StartLine = 7
                    StartColumn = 12
                    EndLine = 7
                    EndColumn = 41
                }
            Severity = "Error"
            FileName = "/project/Math.fs"
            Tag = "FABLE"
        }

    [<Test>]
    let ``fable/project-changed matches its fixture`` () =
        let response =
            ProjectChangedResult.Success (
                [| "/project/Math.fs" ; "/project/Library.fs" |],
                [| diagnostic |],
                [| "/project/App.fsproj" |]
            )

        Assert.That (
            normalize (serialize response),
            Is.EqualTo (normalize (File.ReadAllText (fixture "project-changed.json")))
        )

    [<Test>]
    let ``fable/initial-compile matches its fixture`` () =
        let response =
            FilesCompiledResult.Success (
                Map.ofList [ "/project/Math.fs", "export const sum = 1;" ],
                [| fableDiagnostic |]
            )

        Assert.That (
            normalize (serialize response),
            Is.EqualTo (normalize (File.ReadAllText (fixture "initial-compile.json")))
        )

    [<Test>]
    let ``fable/compile matches its fixture`` () =
        let response =
            FileChangedResult.Success (
                Map.ofList [ "/project/Math.fs", "export const sum = 2;" ],
                [| diagnostic ; fableDiagnostic |]
            )

        Assert.That (normalize (serialize response), Is.EqualTo (normalize (File.ReadAllText (fixture "compile.json"))))

    [<Test>]
    let ``a failure matches its fixture`` () =
        let response = ProjectChangedResult.Error "Could not crack the project."

        Assert.That (normalize (serialize response), Is.EqualTo (normalize (File.ReadAllText (fixture "error.json"))))

/// The debug server is what a tool that is not the plugin can ask what the daemon is doing. It
/// serves the last published snapshot rather than asking the message loop, so these have to answer
/// whether or not anything has been compiled, and without a compile running to answer them.
module DebugServerTests =

    open System.Net
    open System.Net.Http
    open System.Net.Sockets
    open System.Text.Json
    open System.Threading
    open Microsoft.Extensions.Logging

    /// A port nobody else holds, so the suite does not fight the 9014 a running dev server uses.
    let private freePort () : uint16 =
        use listener = new TcpListener (IPAddress.Loopback, 0)
        listener.Start ()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop ()
        uint16 port

    let private sampleProjectDir =
        Path.CombineNormalize (__SOURCE_DIRECTORY__, "../../../sample-project")

    let private mathFs = Path.CombineNormalize (sampleProjectDir, "Math.fs")
    let private libraryFs = Path.CombineNormalize (sampleProjectDir, "Library.fs")

    let private projectState : Debug.ProjectState =
        {
            Fsproj = Path.CombineNormalize (sampleProjectDir, "App.fsproj")
            Configuration = "Debug"
            FableLibrary = fableLibrary
            Exclude = []
            NoReflection = false
            SourceFiles = [| mathFs ; libraryFs |]
            DependentFiles = [| Path.CombineNormalize (sampleProjectDir, "App.fsproj") |]
            TargetFramework = Some "net10.0"
            OutputType = Some "Library"
            CompilerArgs = [| "--define:FABLE_COMPILER" |]
            ProjectReferences = [| "/nuget/FSharp.Core.dll" |]
            Diagnostics =
                [|
                    {
                        ErrorNumberText = "FS0025"
                        Message = "Incomplete pattern matches on this expression."
                        Range =
                            {
                                StartLine = 3
                                StartColumn = 4
                                EndLine = 3
                                EndColumn = 9
                            }
                        Tag = "FSHARP"
                        Severity = "Warning"
                        FileName = mathFs
                    }
                |]
            CacheReused = false
            CacheReason = "dependentFileHashMismatch"
            CacheDetail = "/project/Directory.Build.props"
            CacheFile = "/project/obj/App.vite-plugin-design-time"
            FableModulesCacheFile = "/project/obj/App.vite-plugin-fable-modules"
        }

    /// Waits for Suave to finish binding: `startWebserver` hands back the server task, not the
    /// promise that it is listening.
    let private waitForServer (client : HttpClient) (baseUrl : string) : Task<unit> =
        task {
            let mutable attempts = 0
            let mutable listening = false

            while not listening && attempts < 100 do
                try
                    let! response = client.GetAsync $"{baseUrl}/api/status"
                    listening <- response.IsSuccessStatusCode
                with _ ->
                    ()

                if not listening then
                    attempts <- attempts + 1
                    do! Task.Delay 50
        }

    let private getJson (client : HttpClient) (url : string) : Task<JsonDocument> =
        task {
            let! body = client.GetStringAsync url
            return JsonDocument.Parse body
        }

    /// One test for the whole lifecycle: the snapshot is process-wide, so what `/api/project`
    /// answers before anything is cracked can only be asserted before something is.
    [<Test>]
    let ``the debug endpoints report what the daemon last did`` () =
        task {
            let port = freePort ()
            let baseUrl = $"http://127.0.0.1:%i{port}"
            use cts = new CancellationTokenSource ()
            let logger = Debug.InMemoryLogger ()

            // A daemon that was killed rather than shut down leaves its discovery file behind, so
            // starting one sweeps the ones whose process is gone. A pid that is genuinely not
            // running, read off the process table rather than assumed: pid 0 is the kernel on
            // macOS and reports itself alive, and spawning something to exit needs a shell that
            // differs per OS.
            let deadPid =
                let running =
                    System.Diagnostics.Process.GetProcesses ()
                    |> Array.map (fun p ->
                        let id = p.Id
                        p.Dispose ()
                        id
                    )
                    |> Set.ofArray

                Seq.initInfinite (fun offset -> 40000 + offset)
                |> Seq.find (fun candidate -> not (running.Contains candidate))

            let discoveryFolder =
                DirectoryInfo (Path.Combine (Path.GetTempPath (), "vite-plugin-fable"))

            discoveryFolder.Create ()

            let stale = Path.Combine (discoveryFolder.FullName, $"daemon-%i{deadPid}.json")

            File.WriteAllText (stale, "{}")

            Async.Start (Debug.startWebserver logger port cts.Token, cts.Token)

            use client = new HttpClient ()
            do! waitForServer client baseUrl

            // Nothing has been cracked, and the endpoints still answer.
            use! status = getJson client $"{baseUrl}/api/status"
            Assert.That (status.RootElement.GetProperty("projectLoaded").GetBoolean(), Is.False)
            Assert.That (status.RootElement.GetProperty("port").GetInt32(), Is.EqualTo (int port))

            let! beforeCrack = client.GetAsync $"{baseUrl}/api/project"

            Assert.That (
                beforeCrack.StatusCode,
                Is.EqualTo HttpStatusCode.Conflict,
                "a project nobody cracked has to say so rather than answer with nothing"
            )

            Debug.publishProject projectState

            use! project = getJson client $"{baseUrl}/api/project"
            Assert.That (project.RootElement.GetProperty("sourceFiles").GetArrayLength(), Is.EqualTo 2)
            Assert.That (project.RootElement.GetProperty("targetFramework").GetString(), Is.EqualTo "net10.0")
            // Bulk that a caller has to ask for, so the default answer stays readable.
            Assert.That (project.RootElement.GetProperty("compilerArgs").GetArrayLength(), Is.EqualTo 0)
            Assert.That (project.RootElement.GetProperty("compilerArgCount").GetInt32(), Is.EqualTo 1)

            use! withArgs = getJson client $"{baseUrl}/api/project?include=args,references"
            Assert.That (withArgs.RootElement.GetProperty("compilerArgs").GetArrayLength(), Is.EqualTo 1)
            Assert.That (withArgs.RootElement.GetProperty("projectReferences").GetArrayLength(), Is.EqualTo 1)

            // Why the design time build ran is the thing that is only ever logged today.
            use! cache = getJson client $"{baseUrl}/api/cache"
            Assert.That (cache.RootElement.GetProperty("reused").GetBoolean(), Is.False)

            Assert.That (cache.RootElement.GetProperty("reason").GetString(), Is.EqualTo "dependentFileHashMismatch")

            use! diagnostics = getJson client $"{baseUrl}/api/diagnostics"
            Assert.That (diagnostics.RootElement.GetProperty("warningCount").GetInt32(), Is.EqualTo 1)

            use! errorsOnly = getJson client $"{baseUrl}/api/diagnostics?severity=error"
            Assert.That (errorsOnly.RootElement.GetProperty("count").GetInt32(), Is.EqualTo 0)

            // Before a compile the files are known but none of them has any JavaScript.
            use! files = getJson client $"{baseUrl}/api/files"
            Assert.That (files.RootElement.GetProperty("count").GetInt32(), Is.EqualTo 2)

            Assert.That (files.RootElement.GetProperty("files").[0].GetProperty("compiled").GetBoolean(), Is.False)

            // A file that type-checks but that Fable cannot translate reports nothing to the
            // type-check, so the compile is the only place its error can come from.
            let fableError : Diagnostic =
                {
                    ErrorNumberText = ""
                    Message = "Microsoft.FSharp.Control.FSharpAsync.RunSynchronously (static) is not supported by Fable"
                    Range =
                        {
                            StartLine = 7
                            StartColumn = 12
                            EndLine = 7
                            EndColumn = 41
                        }
                    Severity = "Error"
                    FileName = mathFs
                    Tag = "FABLE"
                }

            Debug.publishInitialCompile (Map.ofList [ mathFs, "export const sum = 1;" ]) Set.empty [| fableError |]

            use! compileErrors = getJson client $"{baseUrl}/api/diagnostics?severity=error"
            Assert.That (compileErrors.RootElement.GetProperty("count").GetInt32(), Is.EqualTo 1)

            Assert.That (
                compileErrors.RootElement.GetProperty("diagnostics").[0].GetProperty("source").GetString(),
                Is.EqualTo "compile"
            )

            use! compiled = getJson client $"{baseUrl}/api/files?path=Math.fs"

            Assert.That (
                compiled.RootElement.GetProperty("javaScript").GetString(),
                Is.EqualTo "export const sum = 1;",
                "asking for one file by its project-relative path has to reach the same file"
            )

            use! withoutSource = getJson client $"{baseUrl}/api/files?path={mathFs}&source=false"

            Assert.That (
                withoutSource.RootElement.GetProperty("javaScript").ValueKind,
                Is.EqualTo JsonValueKind.Null,
                "a caller that only wants the summary should not be handed the whole module"
            )

            let! unknown = client.GetAsync $"{baseUrl}/api/files?path=NotInTheProject.fs"
            Assert.That (unknown.StatusCode, Is.EqualTo HttpStatusCode.NotFound)

            // The revision is how a caller tells whether it is looking at its own edit.
            use! afterCompile = getJson client $"{baseUrl}/api/status"

            Assert.That (
                afterCompile.RootElement.GetProperty("revision").GetInt32(),
                Is.GreaterThan 0,
                "every published message has to move the revision"
            )

            Debug.recordRequest "fable/compile" mathFs 12.5 "success: 1 files"
            use! requests = getJson client $"{baseUrl}/api/requests"
            Assert.That (requests.RootElement.GetProperty("count").GetInt32(), Is.GreaterThan 0)

            // A count alone passes just as happily when every entry serialises to `{}`, which is
            // what an F# record hidden by the signature file does.
            Assert.That (
                requests.RootElement.GetProperty("requests").[0].GetProperty("method").GetString(),
                Is.EqualTo "fable/compile"
            )

            Assert.That (
                requests.RootElement.GetProperty("requests").[0].GetProperty("durationMs").GetDouble(),
                Is.EqualTo 12.5
            )

            (logger :> ILogger).LogInformation "hello from the daemon"
            use! logs = getJson client $"{baseUrl}/api/logs"
            Assert.That (logs.RootElement.GetProperty("total").GetInt32(), Is.GreaterThan 0)

            let nextSince = logs.RootElement.GetProperty("nextSince").GetInt32()
            use! nothingNew = getJson client $"{baseUrl}/api/logs?since={nextSince}"

            Assert.That (
                nothingNew.RootElement.GetProperty("count").GetInt32(),
                Is.EqualTo 0,
                "resuming from nextSince has to return only what arrived after it"
            )

            Assert.That (
                File.Exists stale,
                Is.False,
                "a discovery file for a process that no longer exists was left behind"
            )

            cts.Cancel ()
        }
