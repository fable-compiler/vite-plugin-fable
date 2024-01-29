﻿module Fable.Daemon.CoolCatCracking

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open Fable
open Fable.Compiler.ProjectCracker

let fsharpFiles = set [| ".fs" ; ".fsi" ; ".fsx" |]

let isFSharpFile (file : string) =
    Set.exists (fun (ext : string) -> file.EndsWith (ext, StringComparison.Ordinal)) fsharpFiles


/// Add additional Fable arguments
let private mkOptions (projectFile : FileInfo) (compilerArgs : string array) : string array =
    let arguments =
        compilerArgs
        |> Array.map (fun (line : string) ->
            if not (isFSharpFile line) then
                line
            else
                Path.Combine (projectFile.DirectoryName, line)
        )

    [|
        yield! arguments
        yield "--define:FABLE_COMPILER"
        yield "--define:FABLE_COMPILER_4"
        yield "--define:FABLE_COMPILER_JAVASCRIPT"
    |]

type FullPath = string

let private dotnet_msbuild (fsproj : FullPath) (args : string) : Async<string> =
    backgroundTask {
        let psi = ProcessStartInfo "dotnet"
        let pwd = Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
        psi.WorkingDirectory <- pwd
        psi.Arguments <- $"msbuild \"%s{fsproj}\" %s{args}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use ps = new Process ()
        ps.StartInfo <- psi
        ps.Start () |> ignore
        let output = ps.StandardOutput.ReadToEnd ()
        let error = ps.StandardError.ReadToEnd ()
        do! ps.WaitForExitAsync ()

        if not (String.IsNullOrWhiteSpace error) then
            failwithf $"In %s{pwd}:\ndotnet %s{args} failed with\n%s{error}"

        return output.Trim ()
    }
    |> Async.AwaitTask

let mkOptionsFromDesignTimeBuildAux (fsproj : FileInfo) (options : CrackerOptions) : Async<ProjectOptionsResponse> =
    async {
        let! targetFrameworkJson =
            let configuration =
                if String.IsNullOrWhiteSpace options.Configuration then
                    ""
                else
                    $"/p:Configuration=%s{options.Configuration}"

            dotnet_msbuild
                fsproj.FullName
                $"{configuration} --getProperty:TargetFrameworks --getProperty:TargetFramework --getProperty:DefineConstants"

        let defineConstants, targetFramework =
            let properties =
                JsonDocument.Parse targetFrameworkJson
                |> fun json -> json.RootElement.GetProperty "Properties"

            let defineConstants =
                properties.GetProperty("DefineConstants").GetString().Split ';'
                |> Array.filter (fun c -> c <> "DEBUG" || c <> "RELEASE")

            let tf, tfs =
                properties.GetProperty("TargetFramework").GetString (),
                properties.GetProperty("TargetFrameworks").GetString ()

            if not (String.IsNullOrWhiteSpace tf) then
                defineConstants, tf
            else
                defineConstants, tfs.Split ';' |> Array.head

        let defines =
            [
                "TRACE"
                if not (String.IsNullOrWhiteSpace options.Configuration) then
                    options.Configuration.ToUpper ()
                yield! defineConstants
                yield! options.FableOptions.Define
            ]

            |> List.map (fun s -> s.Trim ())
            // Escaped `;`
            |> String.concat "%3B"

        let version = DateTime.UtcNow.Ticks % 3600L

        let properties =
            [
                "/p:VitePlugin=True"
                if not (String.IsNullOrWhiteSpace options.Configuration) then
                    $"/p:Configuration=%s{options.Configuration}"
                if not (String.IsNullOrWhiteSpace defines) then
                    $"/p:DefineConstants=\"%s{defines}\""
                $"/p:TargetFramework=%s{targetFramework}"
                "/p:DesignTimeBuild=True"
                "/p:SkipCompilerExecution=True"
                "/p:ProvideCommandLineArgs=True"
                // See https://github.com/NuGet/Home/issues/13046
                "/p:RestoreUseStaticGraphEvaluation=False"
                // Avoid restoring with an existing lock file
                "/p:RestoreLockedMode=false"
                "/p:RestorePackagesWithLockFile=false"
                // We trick NuGet into believing there is no lock file create, if it does exist it will try and create it.
                " /p:NuGetLockFilePath=VitePlugin.lock"
                // Pass in a fake version to avoid skipping the CoreCompile target
                $"/p:Version=%i{version}"
            ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> String.concat " "

        let targets =
            "ResolveAssemblyReferencesDesignTime,ResolveProjectReferencesDesignTime,ResolvePackageDependenciesDesignTime,FindReferenceAssembliesForReferences,_GenerateCompileDependencyCache,_ComputeNonExistentFileProperty,BeforeBuild,BeforeCompile,CoreCompile"

        let arguments =
            $"/restore /t:%s{targets} %s{properties}  -warnAsMessage:NU1608 --getItem:FscCommandLineArgs --getItem:ProjectReference --getProperty:OutputType"

        let! json = dotnet_msbuild fsproj.FullName arguments
        let jsonDocument = JsonDocument.Parse json
        let items = jsonDocument.RootElement.GetProperty "Items"
        let properties = jsonDocument.RootElement.GetProperty "Properties"

        let options =
            items.GetProperty("FscCommandLineArgs").EnumerateArray ()
            |> Seq.map (fun arg -> arg.GetProperty("Identity").GetString ())
            |> Seq.toArray

        if Array.isEmpty options then
            return
                failwithf
                    $"Design time build for %s{fsproj.FullName} failed. CoreCompile was most likely skipped. `dotnet clean` might help here."
        else

        let options = mkOptions fsproj options

        let projectReferences =
            items.GetProperty("ProjectReference").EnumerateArray ()
            |> Seq.map (fun arg ->
                let relativePath = arg.GetProperty("Identity").GetString ()

                Path.Combine (fsproj.DirectoryName, relativePath) |> Path.GetFullPath
            )
            |> Seq.toArray

        let outputType = properties.GetProperty("OutputType").GetString ()

        return
            {
                ProjectOptions = options
                ProjectReferences = projectReferences
                OutputType = Some outputType
                TargetFramework = Some targetFramework
            }
    }

let coolCatResolver : ProjectCrackerResolver =
    { new ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            let fsproj = FileInfo projectFile

            if not fsproj.Exists then
                invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

            // Bad I know...
            let result =
                mkOptionsFromDesignTimeBuildAux fsproj options |> Async.RunSynchronously

            result
    }
