module Fable.Daemon.Tests

open System
open System.IO
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
