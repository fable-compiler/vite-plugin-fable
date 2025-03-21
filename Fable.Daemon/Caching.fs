module Fable.Daemon.Caching

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open Microsoft.Extensions.Logging
open Thoth.Json.Core
open Thoth.Json.System.Text.Json
open ProtoBuf
open Fable.Compiler.ProjectCracker

[<Literal>]
let DesignTimeBuildExtension = ".vite-plugin-design-time"

[<Literal>]
let FableModulesExtension = ".vite-plugin-fable-modules"

let fableCompilerVersion =
    let assembly = typeof<CrackerOptions>.Assembly

    let attribute =
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute> ()

    attribute.InformationalVersion

/// Calculates the SHA256 hash of the given file.
type FileInfo with
    member this.Hash : string =
        use sha256 = System.Security.Cryptography.SHA256.Create ()
        use stream = File.OpenRead this.FullName
        let hash = sha256.ComputeHash stream
        BitConverter.ToString(hash).Replace ("-", "")

[<RequireQualifiedAccess>]
type InvalidCacheReason =
    | FileDoesNotExist of cacheFile : FileInfo
    | CouldNotDeserialize of error : string
    | MainFsprojChanged
    | DefinesMismatch of cachedDefines : Set<string> * currentDefines : Set<string>
    | DependentFileCountDoesNotMatch of cachedCount : int * currentCount : int
    | DependentFileHashMismatch of file : FileInfo
    | FableCompilerVersionMismatch of cachedVersion : string * currentVersion : string

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
        /// AssemblyInformationalVersion of Fable.Compiler
        FableCompilerVersion : string
    }

    member x.FableModulesCacheFile =
        Path.ChangeExtension (x.CacheFile.FullName, FableModulesExtension) |> FileInfo

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
    }

let private isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

/// Save the compiler arguments results from the design time build to the intermediate folder.
let writeDesignTimeBuild (x : CacheKey) (response : ProjectOptionsResponse) =
    use fs = File.Create x.CacheFile.FullName

    let dependentFiles =
        [|
            for df in x.DependentFiles do
                yield { Key = df.FullName ; Value = df.Hash }
        |]

    let data =
        {
            MainFsproj = x.MainFsproj.Hash
            DependentFiles = dependentFiles
            Defines = Set.toArray x.Defines
            ProjectOptions = response.ProjectOptions
            ProjectReferences = response.ProjectReferences
            OutputType = response.OutputType
            TargetFramework = response.TargetFramework
            FableCompilerVersion = x.FableCompilerVersion
        }

    Serializer.Serialize (fs, data)

let private emptyArrayIfNull a = if isNull a then Array.empty else a

/// Verify is the cached key for the project exists and is still valid.
let canReuseDesignTimeBuildCache (cacheKey : CacheKey) : Result<ProjectOptionsResponse, InvalidCacheReason> =
    if not cacheKey.CacheFile.Exists then
        Error (InvalidCacheReason.FileDoesNotExist cacheKey.CacheFile)
    else

    try
        use fs = File.OpenRead cacheKey.CacheFile.FullName
        let cacheContent = Serializer.Deserialize<DesignTimeBuildCache> fs
        let cachedDefines = Set.ofArray cacheContent.Defines

        if fableCompilerVersion <> cacheContent.FableCompilerVersion then
            Error (
                InvalidCacheReason.FableCompilerVersionMismatch (
                    cacheContent.FableCompilerVersion,
                    fableCompilerVersion
                )
            )
        elif cacheKey.MainFsproj.Hash <> cacheContent.MainFsproj then
            Error InvalidCacheReason.MainFsprojChanged
        elif cacheKey.Defines <> cachedDefines then
            Error (InvalidCacheReason.DefinesMismatch (cachedDefines, cacheKey.Defines))
        elif cacheKey.DependentFiles.Length <> cacheContent.DependentFiles.Length then
            Error (
                InvalidCacheReason.DependentFileCountDoesNotMatch (
                    cacheContent.DependentFiles.Length,
                    cacheKey.DependentFiles.Length
                )
            )
        else

        // Verify if each dependent files was found in the cached data and if the hashes still match.
        let mismatchedFile =
            (cacheKey.DependentFiles, cacheContent.DependentFiles)
            ||> Seq.zip
            |> Seq.tryFind (fun (df, cachedDF) -> df.FullName <> cachedDF.Key || df.Hash <> cachedDF.Value)
            |> Option.map fst

        match mismatchedFile with
        | Some mmf -> Error (InvalidCacheReason.DependentFileHashMismatch mmf)
        | None ->

        let projectOptionsResponse : ProjectOptionsResponse =
            {
                ProjectOptions = emptyArrayIfNull cacheContent.ProjectOptions
                ProjectReferences = emptyArrayIfNull cacheContent.ProjectReferences
                OutputType = cacheContent.OutputType
                TargetFramework = cacheContent.TargetFramework
            }

        Ok projectOptionsResponse
    with ex ->
        Error (InvalidCacheReason.CouldNotDeserialize ex.Message)

let private cacheKeyDecoder (options : CrackerOptions) (fsproj : FileInfo) : Decoder<CacheKey> =
    Decode.object (fun get ->
        let paths =
            let value = get.Required.At [ "Properties" ; "MSBuildAllProjects" ] Decode.string

            value.Split (';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun path ->
                let fi = FileInfo path

                if not fi.Exists then None else Some fi
            )

        // if `UseArtifactsOutput=true` then the IntermediateOutputPath the path is absolute "C:\Users\nojaf\Projects\telplin\artifacts\obj\OnlineTool\debug"
        // else it is something like "obj\\Release/net7.0/", on Linux slashes can be mixed 🙃
        let intermediateOutputPath =
            let v = get.Required.At [ "Properties" ; "IntermediateOutputPath" ] Decode.string
            let v = if isWindows then v else v.Replace ('\\', '/')
            let v = v.TrimEnd [| '\\' ; '/' |]
            Path.Combine (fsproj.DirectoryName, v) |> Path.GetFullPath

        // Full path of the folder that contains the `g.props` file.
        let msbuildProjectExtensionsPath =
            get.Required.At [ "Properties" ; "MSBuildProjectExtensionsPath" ] Decode.string

        let nugetGProps =
            let gPropFile =
                Path.Combine (msbuildProjectExtensionsPath, $"%s{fsproj.Name}.nuget.g.props")
                |> FileInfo

            if not gPropFile.Exists then [] else [ gPropFile ]

        let cacheFile =
            FileInfo (Path.Combine (intermediateOutputPath, $"{fsproj.Name}%s{DesignTimeBuildExtension}"))

        let dependentFiles =
            [ yield fsproj ; yield! paths ; yield! nugetGProps ]
            |> List.distinctBy (fun fi -> fi.FullName)

        {
            MainFsproj = fsproj
            CacheFile = cacheFile
            DependentFiles = dependentFiles
            Defines = Set.ofList options.FableOptions.Define
            Configuration = options.Configuration
            FableCompilerVersion = fableCompilerVersion
        }
    )

/// Generate the caching key information for the design time build of the incoming fsproj file.
let mkProjectCacheKey
    (logger : ILogger)
    (options : CrackerOptions)
    (fsproj : FileInfo)
    : Async<Result<CacheKey, string>>
    =
    async {
        if not fsproj.Exists then
            raise (ArgumentException ($"%s{fsproj.FullName} does not exists", nameof fsproj))

        if String.IsNullOrWhiteSpace options.Configuration then
            raise (
                ArgumentException ("options.Configuration cannot be null or whitespace", nameof options.Configuration)
            )

        let! json =
            MSBuild.dotnet_msbuild
                logger
                fsproj
                $"/p:Configuration=%s{options.Configuration} --getProperty:MSBuildAllProjects --getProperty:IntermediateOutputPath --getProperty:MSBuildProjectExtensionsPath"

        return Decode.fromString (cacheKeyDecoder options fsproj) json
    }

[<ProtoContract>]
[<CLIMutable>]
type FableModulesProto =
    {
        [<ProtoMember(1)>]
        Files : KeyValuePairProto array
    }

/// Try and load the previous compiled fable-modules files.
/// These should not change if the cache remained stable.
let loadFableModulesFromCache (cacheKey : CacheKey) : Map<FullPath, JavaScript> =
    if not cacheKey.FableModulesCacheFile.Exists then
        Map.empty
    else

    try
        use fs = File.OpenRead cacheKey.FableModulesCacheFile.FullName
        let { Files = files } = Serializer.Deserialize<FableModulesProto> fs

        files
        |> emptyArrayIfNull
        |> Array.map (fun kv -> kv.Key, kv.Value)
        |> Map.ofArray
    with ex ->
        Map.empty

let writeFableModulesFromCache (cacheKey : CacheKey) (fableModuleFiles : Map<FullPath, JavaScript>) =
    try
        let proto : FableModulesProto =
            let files =
                fableModuleFiles.Keys
                |> Seq.map (fun key ->
                    {
                        Key = key
                        Value = fableModuleFiles.[key]
                    }
                )
                |> Seq.toArray

            { Files = files }

        use fs = File.Create cacheKey.FableModulesCacheFile.FullName
        Serializer.Serialize<FableModulesProto> (fs, proto)
    finally
        ()
