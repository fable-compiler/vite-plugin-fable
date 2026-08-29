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
            FilesCompiledResult.Success (Map.ofList [ "/project/Math.fs", "export const sum = 1;" ])

        Assert.That (
            normalize (serialize response),
            Is.EqualTo (normalize (File.ReadAllText (fixture "initial-compile.json")))
        )

    [<Test>]
    let ``fable/compile matches its fixture`` () =
        let response =
            FileChangedResult.Success (Map.ofList [ "/project/Math.fs", "export const sum = 2;" ], [| diagnostic |])

        Assert.That (normalize (serialize response), Is.EqualTo (normalize (File.ReadAllText (fixture "compile.json"))))

    [<Test>]
    let ``a failure matches its fixture`` () =
        let response = ProjectChangedResult.Error "Could not crack the project."

        Assert.That (normalize (serialize response), Is.EqualTo (normalize (File.ReadAllText (fixture "error.json"))))
