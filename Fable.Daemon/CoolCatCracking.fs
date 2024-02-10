﻿namespace Fable.Daemon

open System
open System.IO
open System.Collections.Concurrent
open Thoth.Json.Core
open Thoth.Json.SystemTextJson
open Fable
open Fable.Compiler.ProjectCracker

module CoolCatCracking =

    let fsharpFiles = set [| ".fs" ; ".fsi" ; ".fsx" |]

    let isFSharpFile (file : string) =
        Set.exists (fun (ext : string) -> file.EndsWith (ext, StringComparison.Ordinal)) fsharpFiles

    /// Transform F# files into full paths
    let private mkOptions (projectFile : FileInfo) (compilerArgs : string array) : string array =
        compilerArgs
        |> Array.map (fun (line : string) ->
            if not (isFSharpFile line) then
                line
            else
                Path.Combine (projectFile.DirectoryName, line) |> Path.GetFullPath
        )

    let private identityDecoder =
        Decode.object (fun get -> get.Required.Field "Identity" Decode.string)

    /// Perform a design time build using the `dotnet msbuild` cli invocation.
    let mkOptionsFromDesignTimeBuildAux (fsproj : FileInfo) (options : CrackerOptions) : Async<ProjectOptionsResponse> =
        async {
            let! targetFrameworkJson =
                let configuration =
                    if String.IsNullOrWhiteSpace options.Configuration then
                        ""
                    else
                        $"/p:Configuration=%s{options.Configuration}"

                MSBuild.dotnet_msbuild
                    fsproj
                    $"{configuration} --getProperty:TargetFrameworks --getProperty:TargetFramework --getProperty:DefineConstants"

            // To perform a design time build we need to target an exact single TargetFramework
            // There is a slight chance that the fsproj uses <TargetFrameworks>net8.0</TargetFrameworks>
            // We need to take this into account.
            let defineConstants, targetFramework =
                let decoder =
                    Decode.object (fun get ->
                        get.Required.At [ "Properties" ; "DefineConstants" ] Decode.string,
                        get.Required.At [ "Properties" ; "TargetFramework" ] Decode.string,
                        get.Required.At [ "Properties" ; "TargetFrameworks" ] Decode.string
                    )

                match Decode.fromString decoder targetFrameworkJson with
                | Error e -> failwithf $"Could not decode target framework json, %A{e}"
                | Ok (defineConstants, tf, tfs) ->

                let defineConstants =
                    defineConstants.Split ';'
                    |> Array.filter (fun c -> c <> "DEBUG" || c <> "RELEASE")

                if not (String.IsNullOrWhiteSpace tf) then
                    defineConstants, tf
                else
                    defineConstants, tfs.Split ';' |> Array.head

            // TRACE is typically present for fsproj projects
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

            // When CoreCompile does not need a rebuild, MSBuild will skip that target and thus will not populate the FscCommandLineArgs items.
            // To overcome this we want to force a design time build, using the NonExistentFile property helps prevent a cache hit.
            let nonExistentFile = Path.Combine ("__NonExistentSubDir__", "__NonExistentFile__")

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
                    // This will populate FscCommandLineArgs
                    "/p:ProvideCommandLineArgs=True"
                    // See https://github.com/NuGet/Home/issues/13046
                    "/p:RestoreUseStaticGraphEvaluation=False"
                    // Avoid restoring with an existing lock file
                    "/p:RestoreLockedMode=false"
                    "/p:RestorePackagesWithLockFile=false"
                    // We trick NuGet into believing there is no lock file create, if it does exist it will try and create it.
                    " /p:NuGetLockFilePath=VitePlugin.lock"
                    // Avoid skipping the CoreCompile target via this property.
                    $"/p:NonExistentFile=\"%s{nonExistentFile}\""
                ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " "

            // We do not specify the Restore target itself, the `/restore` flag will take care of this.
            // Imagine with me for a moment how MSBuild works for a given project:
            //
            // it opens the project file
            // it reads and loads the MSBuild SDKs specified in the project file
            // it follows any Imports in those props/targets
            // it then executes the targets involved
            // this is why the /restore flag exists - this tells MSBuild-the-engine to do an entirely separate call to /t:Restore before whatever you specified,
            // so that the targets you specified run against a fully-correct local environment with all the props/targets files
            let targets =
                "ResolveAssemblyReferencesDesignTime,ResolveProjectReferencesDesignTime,ResolvePackageDependenciesDesignTime,FindReferenceAssembliesForReferences,_GenerateCompileDependencyCache,_ComputeNonExistentFileProperty,BeforeBuild,BeforeCompile,CoreCompile"

            // NU1608: Detected package version outside of dependency constraint, see https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1608
            let arguments =
                $"/restore /t:%s{targets} %s{properties}  -warnAsMessage:NU1608 --getItem:FscCommandLineArgs --getItem:ProjectReference --getProperty:OutputType"

            let! json = MSBuild.dotnet_msbuild fsproj arguments

            let decoder =
                Decode.object (fun get ->
                    let options =
                        get.Required.At [ "Items" ; "FscCommandLineArgs" ] (Decode.array identityDecoder)

                    let projectReferences =
                        get.Required.At [ "Items" ; "ProjectReference" ] (Decode.array identityDecoder)

                    let outputType = get.Required.At [ "Properties" ; "OutputType" ] Decode.string
                    options, projectReferences, outputType
                )

            match Decode.fromString decoder json with
            | Error e -> return failwithf $"Could not decode the design time build json, %A{e}"
            | Ok (options, projectReferences, outputType) ->

            if Array.isEmpty options then
                return
                    failwithf
                        $"Design time build for %s{fsproj.FullName} failed. CoreCompile was most likely skipped. `dotnet clean` might help here.\ndotnet msbuild %s{fsproj.FullName} %s{arguments}"
            else

            let options = mkOptions fsproj options

            let projectReferences =
                projectReferences
                |> Seq.map (fun relativePath -> Path.Combine (fsproj.DirectoryName, relativePath) |> Path.GetFullPath)
                |> Seq.toArray

            return
                {
                    ProjectOptions = options
                    ProjectReferences = projectReferences
                    OutputType = Some outputType
                    TargetFramework = Some targetFramework
                }
        }

/// Crack the fsproj using the `dotnet msbuild --getProperty --getItem` command
/// See https://devblogs.microsoft.com/dotnet/announcing-dotnet-8-rc2/#msbuild-simple-cli-based-project-evaluation
type CoolCatResolver() =
    let cached = ConcurrentDictionary<FullPath, Caching.CacheKey> ()

    /// Under the same design time conditions and same Fable.Compiler, the used Fable libraries don't change.
    member x.TryGetCachedFableModuleFiles (fsproj : FullPath) : Map<FullPath, string> =
        if not (cached.ContainsKey fsproj) then
            Map.empty
        else
            Caching.loadFableModulesFromCache cached.[fsproj]

    /// Try and write the fable_module compilation results to the cache.
    member x.WriteCachedFableModuleFiles (fsproj : FullPath) (fableModuleFiles : Map<FullPath, JavaScript>) =
        if not (cached.ContainsKey fsproj) then
            ()
        else

        Caching.writeFableModulesFromCache cached.[fsproj] fableModuleFiles

    interface ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            async {
                let fsproj = FileInfo projectFile

                if not fsproj.Exists then
                    invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

                let! currentCacheKey =
                    async {
                        if cached.ContainsKey fsproj.FullName then
                            return cached.[fsproj.FullName]
                        else
                            match! Caching.mkProjectCacheKey options fsproj with
                            | Error error ->
                                return failwithf $"Could not construct cache key for %s{projectFile}, %A{error}"
                            | Ok cacheKey -> return cacheKey
                    }

                cached.AddOrUpdate (fsproj.FullName, (fun _ -> currentCacheKey), (fun _ _ -> currentCacheKey))
                |> ignore

                match Caching.canReuseDesignTimeBuildCache currentCacheKey with
                | Ok projectOptionsResponse ->
                    // The sweet spot, nothing changed and we can skip the design time build
                    return projectOptionsResponse
                | Error reason ->
                    // Delete the current cache file if it is no longer valid.
                    match reason with
                    | Caching.InvalidCacheReason.CouldNotDeserialize _
                    | Caching.InvalidCacheReason.MainFsprojChanged
                    | Caching.InvalidCacheReason.DefinesMismatch _
                    | Caching.InvalidCacheReason.DependentFileCountDoesNotMatch _
                    | Caching.InvalidCacheReason.DependentFileHashMismatch _ ->
                        try
                            File.Delete currentCacheKey.CacheFile.FullName
                            File.Delete currentCacheKey.FableModulesCacheFile.FullName
                        finally
                            ()
                    | Caching.InvalidCacheReason.FileDoesNotExist _ -> ()

                    // Perform design time build and cache result
                    let! result = CoolCatCracking.mkOptionsFromDesignTimeBuildAux fsproj options
                    Caching.writeDesignTimeBuild currentCacheKey result

                    return result
            }
            |> Async.RunSynchronously
