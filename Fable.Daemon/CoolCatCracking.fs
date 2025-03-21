namespace Fable.Daemon

open System
open System.IO
open System.Collections.Concurrent
open Microsoft.Extensions.Logging
open Thoth.Json.Core
open Thoth.Json.System.Text.Json
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
    let mkOptionsFromDesignTimeBuildAux
        (logger : ILogger)
        (fsproj : FileInfo)
        (options : CrackerOptions)
        : Async<ProjectOptionsResponse>
        =
        async {
            let! targetFrameworkJson =
                let configuration =
                    if String.IsNullOrWhiteSpace options.Configuration then
                        ""
                    else
                        $"/p:Configuration=%s{options.Configuration}"

                MSBuild.dotnet_msbuild
                    logger
                    fsproj
                    $"{configuration} --getProperty:TargetFrameworks --getProperty:TargetFramework"

            // To perform a design time build we need to target an exact single TargetFramework
            // There is a slight chance that the fsproj uses <TargetFrameworks>net8.0</TargetFrameworks>
            // We need to take this into account.
            let targetFramework =
                let decoder =
                    Decode.object (fun get ->
                        get.Required.At [ "Properties" ; "TargetFramework" ] Decode.string,
                        get.Required.At [ "Properties" ; "TargetFrameworks" ] Decode.string
                    )

                match Decode.fromString decoder targetFrameworkJson with
                | Error e -> failwithf $"Could not decode target framework json, %A{e}"
                | Ok (tf, tfs) ->

                if not (String.IsNullOrWhiteSpace tf) then
                    tf
                else
                    tfs.Split ';' |> Array.head

            logger.LogDebug ("Perform design time build for {targetFramework}", targetFramework)

            // TRACE is typically present for fsproj projects
            let defines = options.FableOptions.Define

            // When CoreCompile does not need a rebuild, MSBuild will skip that target and thus will not populate the FscCommandLineArgs items.
            // To overcome this we want to force a design time build, using the NonExistentFile property helps prevent a cache hit.
            let nonExistentFile = Path.Combine ("__NonExistentSubDir__", "__NonExistentFile__")

            let properties =
                [
                    "/p:VitePlugin=True"
                    if not (String.IsNullOrWhiteSpace options.Configuration) then
                        $"/p:Configuration=%s{options.Configuration}"
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
                $"/restore /t:%s{targets} %s{properties}  -warnAsMessage:NU1608 -warnAsMessage:NU1605 --getItem:FscCommandLineArgs --getItem:ProjectReference --getProperty:OutputType"

            let! json = MSBuild.dotnet_msbuild_with_defines logger fsproj arguments defines

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
                logger.LogCritical (
                    "Design time build for {fsproj} failed. CoreCompile was most likely skipped.",
                    fsproj.FullName
                )

                return
                    failwithf
                        $"Design time build for %s{fsproj.FullName} failed. CoreCompile was most likely skipped. `dotnet clean` might help here.\ndotnet msbuild %s{fsproj.FullName} %s{arguments}"
            else

            logger.LogDebug ("Design time build for {fsproj} completed.", fsproj)
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
type CoolCatResolver(logger : ILogger) =
    let cached = ConcurrentDictionary<FullPath, Caching.CacheKey> ()

    /// Under the same design time conditions and same Fable.Compiler, the used Fable libraries don't change.
    member x.TryGetCachedFableModuleFiles (fsproj : FullPath) : Map<FullPath, string> =
        if not (cached.ContainsKey fsproj) then
            logger.LogWarning ("{fsproj} does not have a cache entry in CoolCatResolver", fsproj)
            Map.empty
        else
            Caching.loadFableModulesFromCache cached.[fsproj]

    /// Try and write the fable_module compilation results to the cache.
    member x.WriteCachedFableModuleFiles (fsproj : FullPath) (fableModuleFiles : Map<FullPath, JavaScript>) =
        if not (cached.ContainsKey fsproj) then
            logger.LogWarning ("{fsproj} does not have a cache entry in CoolCatResolver", fsproj)
        else

        Caching.writeFableModulesFromCache cached.[fsproj] fableModuleFiles

    /// Get project files to watch inside the plugin
    /// These are the fsproj and potential MSBuild import files
    member x.MSBuildProjectFiles (fsproj : FullPath) : FileInfo list =
        if not (cached.ContainsKey fsproj) then
            logger.LogWarning ("{fsproj} does not have a cache entry in CoolCatResolver", fsproj)
            List.empty
        else
            cached.[fsproj].DependentFiles

    interface ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            async {
                logger.LogDebug ("ProjectCrackerResolver.GetProjectOptionsFromProjectFile {projectFile}", projectFile)
                let fsproj = FileInfo projectFile

                if not fsproj.Exists then
                    invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

                let! currentCacheKey =
                    async {
                        if cached.ContainsKey fsproj.FullName then
                            return cached.[fsproj.FullName]
                        else
                            match! Caching.mkProjectCacheKey logger options fsproj with
                            | Error error ->
                                logger.LogError (
                                    "Could not construct cache key for {projectFile} {error}",
                                    projectFile,
                                    error
                                )

                                return failwithf $"Could not construct cache key for %s{projectFile}, %A{error}"
                            | Ok cacheKey -> return cacheKey
                    }

                cached.AddOrUpdate (fsproj.FullName, (fun _ -> currentCacheKey), (fun _ _ -> currentCacheKey))
                |> ignore

                match Caching.canReuseDesignTimeBuildCache currentCacheKey with
                | Ok projectOptionsResponse ->
                    logger.LogInformation ("Design time build cache can be reused for {projectFile}", projectFile)
                    // The sweet spot, nothing changed and we can skip the design time build
                    return projectOptionsResponse
                | Error reason ->
                    logger.LogDebug (
                        "Cache file could not be reused for {projectFile} because {reason}",
                        projectFile,
                        reason
                    )

                    // Delete the current cache file if it is no longer valid.
                    match reason with
                    | Caching.InvalidCacheReason.CouldNotDeserialize _
                    | Caching.InvalidCacheReason.FableCompilerVersionMismatch _
                    | Caching.InvalidCacheReason.MainFsprojChanged
                    | Caching.InvalidCacheReason.DefinesMismatch _
                    | Caching.InvalidCacheReason.DependentFileCountDoesNotMatch _
                    | Caching.InvalidCacheReason.DependentFileHashMismatch _ ->
                        try
                            if currentCacheKey.CacheFile.Exists then
                                File.Delete currentCacheKey.CacheFile.FullName

                            if currentCacheKey.FableModulesCacheFile.Exists then
                                File.Delete currentCacheKey.FableModulesCacheFile.FullName
                        finally
                            ()
                    | Caching.InvalidCacheReason.FileDoesNotExist _ -> ()

                    // Perform design time build and cache result
                    logger.LogDebug ("About to perform design time build for {projectFile}", projectFile)
                    let! result = CoolCatCracking.mkOptionsFromDesignTimeBuildAux logger fsproj options
                    Caching.writeDesignTimeBuild currentCacheKey result

                    return result
            }
            |> Async.RunSynchronously
