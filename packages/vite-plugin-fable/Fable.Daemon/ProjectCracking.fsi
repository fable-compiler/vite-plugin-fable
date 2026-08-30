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
type CachedMSBuildCrackerResolver =
    new : logger : ILogger -> CachedMSBuildCrackerResolver
    /// Under the same design time conditions and same Fable.Compiler, the used Fable libraries don't change.
    member TryGetCachedFableModuleFiles : fsproj : FullPath -> Map<FullPath, JavaScript>
    /// Try and write the fable_module compilation results to the cache.
    member WriteCachedFableModuleFiles : fsproj : FullPath -> fableModuleFiles : Map<FullPath, JavaScript> -> unit
    /// Drop the remembered cache keys, so the next crack asks MSBuild again which files its
    /// evaluation depends on.
    ///
    /// A key is worth remembering for the length of one crack, where the same project can be
    /// visited more than once, but not beyond it: `MSBuildAllProjects` is only re-read when a key
    /// is built, so a key kept across cracks describes the project as it was. Add a
    /// `Directory.Build.props` or an `<Import>` and the stale key compares the new project against
    /// the old one's inputs, finds nothing changed, and reuses a design time build that predates
    /// the file. The new file is also never reported to the plugin, so nothing watches it.
    member ForgetCacheKeys : unit -> unit
    /// The cache key the last crack built for this project, which names the files its MSBuild
    /// evaluation depends on and where the cached design time build lives.
    member TryGetCacheKey : fsproj : FullPath -> Caching.CacheKey option
    /// Whether the last crack of this project reused its design time build cache, and if not, why.
    member CacheDecision : fsproj : FullPath -> Result<unit, Caching.InvalidCacheReason> option
    /// Get project files to watch inside the plugin
    /// These are the fsproj and potential MSBuild import files
    member MSBuildProjectFiles : fsproj : FullPath -> FileInfo list
    interface ProjectCrackerResolver
