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
let DesignTimeBuildExtension = ".vite-plugin-design-time"

[<Literal>]
let FableModulesExtension = ".vite-plugin-fable-modules"

/// Bumped whenever the cached shape changes. A cache written before a field existed deserialises
/// that field as its default, which can look like a match; this makes such a cache invalid instead.
let cacheFormatVersion = 2

let fableCompilerVersion =
    let assembly = typeof<CrackerOptions>.Assembly

    let attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()

    attribute.InformationalVersion

type FileInfo with
    member this.Hash : string =
        use sha256 = System.Security.Cryptography.SHA256.Create ()
        use stream = File.OpenRead this.FullName
        let hash = sha256.ComputeHash stream
        BitConverter.ToString(hash).Replace("-", "")

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

let describeInvalidCacheReason (reason : InvalidCacheReason) : string * string =
    match reason with
    | InvalidCacheReason.FileDoesNotExist cacheFile -> "fileDoesNotExist", cacheFile.FullName
    | InvalidCacheReason.CouldNotDeserialize error -> "couldNotDeserialize", error
    | InvalidCacheReason.MainFsprojChanged -> "mainFsprojChanged", ""
    | InvalidCacheReason.DefinesMismatch (cached, current) ->
        "definesMismatch", $"""cached: %s{String.concat ", " cached}, current: %s{String.concat ", " current}"""
    | InvalidCacheReason.DependentFileCountDoesNotMatch (cached, current) ->
        "dependentFileCountDoesNotMatch", $"cached: %i{cached}, current: %i{current}"
    | InvalidCacheReason.DependentFileHashMismatch file -> "dependentFileHashMismatch", file.FullName
    | InvalidCacheReason.FableCompilerVersionMismatch (cached, current) ->
        "fableCompilerVersionMismatch", $"cached: %s{cached}, current: %s{current}"
    | InvalidCacheReason.ExcludeMismatch (cached, current) ->
        "excludeMismatch", $"""cached: %s{String.concat ", " cached}, current: %s{String.concat ", " current}"""
    | InvalidCacheReason.NoReflectionMismatch (cached, current) ->
        "noReflectionMismatch", $"cached: %b{cached}, current: %b{current}"
    | InvalidCacheReason.CacheFormatChanged (cached, current) ->
        "cacheFormatChanged", $"cached: %i{cached}, current: %i{current}"

type CacheKey =
    {
        MainFsproj : FileInfo
        CacheFile : FileInfo
        DependentFiles : FileInfo list
        Defines : Set<string>
        Configuration : string
        Exclude : string list
        NoReflection : bool
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
        [<ProtoMember(9)>]
        Exclude : string array
        [<ProtoMember(10)>]
        NoReflection : bool
        [<ProtoMember(11)>]
        CacheFormatVersion : int
    }

let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

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
            Exclude = List.toArray x.Exclude
            NoReflection = x.NoReflection
            CacheFormatVersion = cacheFormatVersion
        }

    Serializer.Serialize (fs, data)

let emptyArrayIfNull a = if isNull a then Array.empty else a

let canReuseDesignTimeBuildCache (cacheKey : CacheKey) : Result<ProjectOptionsResponse, InvalidCacheReason> =
    if not cacheKey.CacheFile.Exists then
        Error (InvalidCacheReason.FileDoesNotExist cacheKey.CacheFile)
    else

    try
        use fs = File.OpenRead cacheKey.CacheFile.FullName
        let cacheContent = Serializer.Deserialize<DesignTimeBuildCache> fs
        let cachedDefines = Set.ofArray cacheContent.Defines

        if cacheContent.CacheFormatVersion <> cacheFormatVersion then
            Error (InvalidCacheReason.CacheFormatChanged (cacheContent.CacheFormatVersion, cacheFormatVersion))
        elif fableCompilerVersion <> cacheContent.FableCompilerVersion then
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
        elif cacheKey.Exclude <> List.ofArray (emptyArrayIfNull cacheContent.Exclude) then
            Error (
                InvalidCacheReason.ExcludeMismatch (
                    List.ofArray (emptyArrayIfNull cacheContent.Exclude),
                    cacheKey.Exclude
                )
            )
        elif cacheKey.NoReflection <> cacheContent.NoReflection then
            Error (InvalidCacheReason.NoReflectionMismatch (cacheContent.NoReflection, cacheKey.NoReflection))
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

let decodeCacheKey (options : CrackerOptions) (fsproj : FileInfo) (json : string) : Result<CacheKey, string> =
    try
        use document = JsonDocument.Parse json
        let properties = document.RootElement.GetProperty "Properties"

        let getProperty (name : string) =
            properties.GetProperty(name).GetString()

        /// MSBuild answers with an empty string for a property it has no value for, so this is only
        /// about a property that was never asked for.
        let tryGetProperty (name : string) : string =
            match properties.TryGetProperty name with
            | true, value -> value.GetString () |> Option.ofObj |> Option.defaultValue ""
            | false, _ -> ""

        let paths =
            (getProperty "MSBuildAllProjects").Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun path ->
                let fi = FileInfo path

                if not fi.Exists then None else Some fi
            )

        // if `UseArtifactsOutput=true` then the IntermediateOutputPath the path is absolute "C:\Users\nojaf\Projects\telplin\artifacts\obj\OnlineTool\debug"
        // else it is something like "obj\\Release/net7.0/", on Linux slashes can be mixed 🙃
        let intermediateOutputPath =
            let v = getProperty "IntermediateOutputPath"
            let v = if isWindows then v else v.Replace ('\\', '/')
            let v = v.TrimEnd [| '\\' ; '/' |]
            Path.Combine (fsproj.DirectoryName, v) |> Path.GetFullPath

        // Full path of the folder that contains the `g.props` file.
        let msbuildProjectExtensionsPath = getProperty "MSBuildProjectExtensionsPath"

        let nugetGProps =
            let gPropFile =
                Path.Combine (msbuildProjectExtensionsPath, $"%s{fsproj.Name}.nuget.g.props")
                |> FileInfo

            if not gPropFile.Exists then [] else [ gPropFile ]

        let cacheFile =
            FileInfo (Path.Combine (intermediateOutputPath, $"{fsproj.Name}%s{DesignTimeBuildExtension}"))

        /// The files MSBuild imports by convention rather than by an `<Import>` in the project.
        ///
        /// They have to be asked for by name: since MSBuild 16.9 imports no longer add themselves
        /// to `MSBuildAllProjects`, so a `Directory.Build.props` does not appear there at all.
        /// Without these, editing one changed nothing the cache key knew about, the design time
        /// build was reused, and the plugin was never told to watch the file either.
        let conventionImports =
            [
                yield "DirectoryBuildPropsPath"
                yield "DirectoryBuildTargetsPath"
                // The SDK resolves this whether or not central package management is on. When it is
                // off the file is found and ignored, and watching it would re-crack the project for
                // an edit that cannot change anything.
                if
                    String.Equals (
                        tryGetProperty "ManagePackageVersionsCentrally",
                        "true",
                        StringComparison.OrdinalIgnoreCase
                    )
                then
                    yield "DirectoryPackagesPropsPath"
            ]
            |> List.choose (fun property ->
                let path = tryGetProperty property

                if String.IsNullOrWhiteSpace path then
                    None
                else

                let fi = FileInfo path
                if fi.Exists then Some fi else None
            )

        let dependentFiles =
            [ yield fsproj ; yield! paths ; yield! conventionImports ; yield! nugetGProps ]
            |> List.distinctBy (fun fi -> fi.FullName)

        Ok
            {
                MainFsproj = fsproj
                CacheFile = cacheFile
                DependentFiles = dependentFiles
                Defines = Set.ofList options.FableOptions.Define
                Configuration = options.Configuration
                Exclude = options.Exclude
                NoReflection = options.FableOptions.NoReflection
                FableCompilerVersion = fableCompilerVersion
            }
    with ex ->
        Error $"Could not decode MSBuild output:\n%s{json}\n%s{ex.Message}"

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
                $"/p:Configuration=%s{options.Configuration} --getProperty:MSBuildAllProjects --getProperty:IntermediateOutputPath --getProperty:MSBuildProjectExtensionsPath --getProperty:DirectoryBuildPropsPath --getProperty:DirectoryBuildTargetsPath --getProperty:DirectoryPackagesPropsPath --getProperty:ManagePackageVersionsCentrally"

        return decodeCacheKey options fsproj json
    }

[<ProtoContract>]
[<CLIMutable>]
type FableModulesProto =
    {
        [<ProtoMember(1)>]
        Files : KeyValuePairProto array
    }

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
        Serializer.Serialize<FableModulesProto>(fs, proto)
    finally
        ()
