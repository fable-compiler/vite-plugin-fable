module Fable.Daemon.Caching

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text.Json
open Microsoft.Extensions.Logging
open ProtoBuf
open Fable.Compiler.ProjectCracker

[<Literal>]
val DesignTimeBuildExtension : string = ".vite-plugin-design-time"

[<Literal>]
val FableModulesExtension : string = ".vite-plugin-fable-modules"

val fableCompilerVersion : string

/// Calculates the SHA256 hash of the given file.
type FileInfo with
    member Hash : string

[<RequireQualifiedAccess>]
type InvalidCacheReason =
    | FileDoesNotExist of cacheFile : FileInfo
    | CouldNotDeserialize of error : string
    | MainFsprojChanged
    | DefinesMismatch of cachedDefines : Set<string> * currentDefines : Set<string>
    | DependentFileCountDoesNotMatch of cachedCount : int * currentCount : int
    | DependentFileHashMismatch of file : FileInfo
    | FableCompilerVersionMismatch of cachedVersion : string * currentVersion : string
    | ExcludeMismatch of cachedExclude : string list * currentExclude : string list
    | NoReflectionMismatch of cachedNoReflection : bool * currentNoReflection : bool
    | CacheFormatChanged of cachedVersion : int * currentVersion : int

/// A stable name for why a design time build cache could not be reused, and the detail behind it.
/// The name is what a tool matches on, so it stays put when the wording of the detail changes.
val describeInvalidCacheReason : reason : InvalidCacheReason -> string * string

/// Contains all the info that determines the cache design time build value.
/// This is not the cached information!
type CacheKey =
    {
        /// Input fsproj project.
        MainFsproj : FileInfo
        /// This is the file that contains the cached information.
        /// Initially it doesn't exist and can only be checked in subsequent runs.
        CacheFile : FileInfo
        /// All the files that can influence the MSBuild evaluation.
        /// This typically is the
        DependentFiles : FileInfo list
        /// Contains both the user defined configurations (via Vite plugin options)
        Defines : Set<string>
        /// Configuration
        Configuration : string
        /// Files excluded from compilation (via Vite plugin options).
        /// Changes what Fable emits, so it has to invalidate the cache.
        Exclude : string list
        /// Whether reflection info is emitted (via Vite plugin options).
        /// Changes what Fable emits, so it has to invalidate the cache.
        NoReflection : bool
        /// AssemblyInformationalVersion of Fable.Compiler
        FableCompilerVersion : string
    }

    member FableModulesCacheFile : FileInfo

[<ProtoContract>]
[<CLIMutable>]
type KeyValuePairProto =
    {
        [<ProtoMember(1)>]
        Key : string
        [<ProtoMember(2)>]
        Value : string
    }

[<ProtoContract>]
[<CLIMutable>]
type DesignTimeBuildCache =
    {
        [<ProtoMember(1)>]
        MainFsproj : string
        [<ProtoMember(2)>]
        DependentFiles : KeyValuePairProto array
        [<ProtoMember(3)>]
        Defines : string array
        [<ProtoMember(4)>]
        ProjectOptions : string array
        [<ProtoMember(5)>]
        ProjectReferences : string array
        [<ProtoMember(6)>]
        OutputType : string option
        [<ProtoMember(7)>]
        TargetFramework : string option
        [<ProtoMember(8)>]
        FableCompilerVersion : string
        [<ProtoMember(9)>]
        Exclude : string array
        [<ProtoMember(10)>]
        NoReflection : bool
        [<ProtoMember(11)>]
        CacheFormatVersion : int
    }

/// Save the compiler arguments results from the design time build to the intermediate folder.
val writeDesignTimeBuild : x : CacheKey -> response : ProjectOptionsResponse -> unit
/// Verify is the cached key for the project exists and is still valid.
val canReuseDesignTimeBuildCache : cacheKey : CacheKey -> Result<ProjectOptionsResponse, InvalidCacheReason>

/// Generate the caching key information for the design time build of the incoming fsproj file.
val mkProjectCacheKey :
    logger : ILogger -> options : CrackerOptions -> fsproj : FileInfo -> Async<Result<CacheKey, string>>

[<ProtoContract>]
[<CLIMutable>]
type FableModulesProto =
    {
        [<ProtoMember(1)>]
        Files : KeyValuePairProto array
    }

/// Try and load the previous compiled fable-modules files.
/// These should not change if the cache remained stable.
val loadFableModulesFromCache : cacheKey : CacheKey -> Map<FullPath, JavaScript>
val writeFableModulesFromCache : cacheKey : CacheKey -> fableModuleFiles : Map<FullPath, JavaScript> -> unit
