namespace Fable.Daemon

open System
open System.IO
open System.Collections.Concurrent
open Microsoft.Extensions.Logging
open Fable.Compiler
open Fable.Compiler.ProjectCracker

/// Wraps Fable's own MSBuildCrackerResolver with a design time build cache.
/// The actual `dotnet msbuild` cracking is done by Fable.Compiler, this type only decides whether it needs to run
/// and remembers the files that influence the MSBuild evaluation so the plugin can watch them.
type CachedMSBuildCrackerResolver(logger : ILogger) =
    let inner = MSBuildCrackerResolver () :> ProjectCrackerResolver
    let cached = ConcurrentDictionary<FullPath, Caching.CacheKey> ()

    let tryGetCacheKey (fsproj : FullPath) =
        match cached.TryGetValue fsproj with
        | true, cacheKey -> Some cacheKey
        | false, _ ->
            logger.LogWarning ("{fsproj} does not have a cache entry in CachedMSBuildCrackerResolver", fsproj)
            None

    /// Under the same design time conditions and same Fable.Compiler, the used Fable libraries don't change.
    member x.TryGetCachedFableModuleFiles (fsproj : FullPath) : Map<FullPath, JavaScript> =
        match tryGetCacheKey fsproj with
        | None -> Map.empty
        | Some cacheKey -> Caching.loadFableModulesFromCache cacheKey

    /// Try and write the fable_module compilation results to the cache.
    member x.WriteCachedFableModuleFiles (fsproj : FullPath) (fableModuleFiles : Map<FullPath, JavaScript>) =
        match tryGetCacheKey fsproj with
        | None -> ()
        | Some cacheKey -> Caching.writeFableModulesFromCache cacheKey fableModuleFiles

    /// Get project files to watch inside the plugin
    /// These are the fsproj and potential MSBuild import files
    member x.MSBuildProjectFiles (fsproj : FullPath) : FileInfo list =
        match tryGetCacheKey fsproj with
        | None -> List.empty
        | Some cacheKey -> cacheKey.DependentFiles

    interface ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            async {
                logger.LogDebug ("ProjectCrackerResolver.GetProjectOptionsFromProjectFile {projectFile}", projectFile)
                let fsproj = FileInfo projectFile

                if not fsproj.Exists then
                    invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

                let! currentCacheKey =
                    async {
                        match cached.TryGetValue fsproj.FullName with
                        | true, cacheKey -> return cacheKey
                        | false, _ ->
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

                cached.[fsproj.FullName] <- currentCacheKey

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
                    | Caching.InvalidCacheReason.FileDoesNotExist _ -> ()
                    | _ ->
                        if currentCacheKey.CacheFile.Exists then
                            File.Delete currentCacheKey.CacheFile.FullName

                        if currentCacheKey.FableModulesCacheFile.Exists then
                            File.Delete currentCacheKey.FableModulesCacheFile.FullName

                    logger.LogDebug ("About to perform design time build for {projectFile}", projectFile)
                    let result = inner.GetProjectOptionsFromProjectFile (isMain, options, projectFile)
                    Caching.writeDesignTimeBuild currentCacheKey result
                    logger.LogDebug ("Design time build for {projectFile} completed.", projectFile)
                    return result
            }
            |> Async.RunSynchronously
