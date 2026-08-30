namespace Fable.Daemon

open System
open System.IO
open System.Collections.Concurrent
open Microsoft.Extensions.Logging
open Fable.Compiler
open Fable.Compiler.ProjectCracker

type CachedMSBuildCrackerResolver(logger : ILogger) =
    let inner = MSBuildCrackerResolver () :> ProjectCrackerResolver
    let cached = ConcurrentDictionary<FullPath, Caching.CacheKey>()

    /// What the last crack decided about each project's design time build cache. Only logged
    /// before, which meant the answer to "why did that crack take 20 seconds" was thrown away.
    let decisions =
        ConcurrentDictionary<FullPath, Result<unit, Caching.InvalidCacheReason>>()

    let tryGetCacheKey (fsproj : FullPath) =
        match cached.TryGetValue fsproj with
        | true, cacheKey -> Some cacheKey
        | false, _ ->
            logger.LogWarning ("{fsproj} does not have a cache entry in CachedMSBuildCrackerResolver", fsproj)
            None

    member x.TryGetCachedFableModuleFiles (fsproj : FullPath) : Map<FullPath, JavaScript> =
        match tryGetCacheKey fsproj with
        | None -> Map.empty
        | Some cacheKey -> Caching.loadFableModulesFromCache cacheKey

    member x.WriteCachedFableModuleFiles (fsproj : FullPath) (fableModuleFiles : Map<FullPath, JavaScript>) =
        match tryGetCacheKey fsproj with
        | None -> ()
        | Some cacheKey -> Caching.writeFableModulesFromCache cacheKey fableModuleFiles

    member x.ForgetCacheKeys () : unit =
        cached.Clear ()
        decisions.Clear ()

    member x.TryGetCacheKey (fsproj : FullPath) : Caching.CacheKey option = tryGetCacheKey fsproj

    member x.CacheDecision (fsproj : FullPath) : Result<unit, Caching.InvalidCacheReason> option =
        match decisions.TryGetValue fsproj with
        | true, decision -> Some decision
        | false, _ -> None

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

                let decision = Caching.canReuseDesignTimeBuildCache currentCacheKey
                decisions.[fsproj.FullName] <- Result.map ignore decision

                match decision with
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
