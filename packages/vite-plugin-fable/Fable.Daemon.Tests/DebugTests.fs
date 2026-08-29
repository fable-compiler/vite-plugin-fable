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
