#I "packages/build/FAKE/tools"
#r "FakeLib.dll"
#I "packages/build/Chessie/lib/net40"
#r "Chessie.dll"
#I "packages/build/Paket.Core/lib/net45"
#r "Paket.Core.dll"

open Fake
open System.IO
open Paket.Constants

let lockFile = 
    __SOURCE_DIRECTORY__ </> "paket.lock"
    |> Paket.LockFile.LoadFrom 

let mainGroup =
    lockFile.GetGroup(MainDependencyGroup)
    
let packages = __SOURCE_DIRECTORY__ </> "packages"
let nupkgPath n v = packages </> n </> (n + "." + v + ".nupkg") 

let packageVersions = 
    mainGroup.Resolution 
    |> Map.toSeq
    |> Seq.map (fun (i, p) ->
        let n = i.Name
        let version = 
            // version in lock file might not be the full one in the file name
            let v = p.Version.AsString
            let n' = nupkgPath n v
            if fileExists n' then v else
            let v = v + ".0"
            let n = nupkgPath n v
            if fileExists n then v else
            v + ".0"
        n, version
    )
    |> List.ofSeq

let pkgFolder = __SOURCE_DIRECTORY__ </> "WebSharper.Vsix/Packages"
CreateDir pkgFolder
CleanDir pkgFolder

packageVersions
|> Seq.iter (fun (n, v) ->
    let nupkgFrom = nupkgPath n v
    let nupkgTo = pkgFolder </> Path.GetFileName nupkgFrom
    nupkgFrom |> CopyFile nupkgTo
) 

let snk, publicKeyToken =
    match environVar "INTELLIFACTORY" with
    | null -> "../tools/WebSharper.snk", "451ee5fa653b377d"
    | p -> p </> "keys/IntelliFactory.snk", "dcd983dec8f76a71"

let revision =
    match environVar "BUILD_NUMBER" with
    | null | "" -> "0"
    | r -> r

let version, tag = 
    let wsVersion =
        packageVersions |> List.pick (function "WebSharper", v -> Some v | _ -> None)
    let withoutTag, tag =
        match wsVersion.IndexOf('-') with
        | -1 -> wsVersion, ""
        | i -> wsVersion.[.. i - 1], wsVersion.[i ..]
    let nums = withoutTag.Split('.')
    (nums.[0 .. 2] |> String.concat ".") + "." + revision, tag

let taggedVersion = version + tag

let replacesInFile replaces p =
    let inp = File.ReadAllText(p)
    let res = (inp, replaces) ||> List.fold (fun s (i: string, o) -> s.Replace(i, o)) 
    let fn = p.[.. p.Length - 4]
    printfn "Created: %s" fn
    File.WriteAllText(fn, res)

let vsixAssembly =
    "WebSharper." + taggedVersion + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=" + publicKeyToken

let vstemplateReplaces =
    [   
        for p, v in packageVersions do
            yield 
                sprintf "package id=\"%s\"" p, 
                sprintf "package id=\"%s\" version=\"%s\"" p v
        yield "{vsixassembly}", vsixAssembly
    ]

Directory.EnumerateFiles(__SOURCE_DIRECTORY__, "*.vstemplate.in", SearchOption.AllDirectories)
|> Seq.iter (replacesInFile vstemplateReplaces)

__SOURCE_DIRECTORY__ </> "WebSharper.Vsix/WebSharper.Vsix.csproj.in" |> replacesInFile [   
        for p, v in packageVersions do
            yield
                sprintf "Include=\"Packages\\%s.nupkg\"" p, 
                sprintf "Include=\"Packages\\%s.%s.nupkg\"" p v
        yield "{vsixversion}", taggedVersion
        yield "{keyfilepath}", snk
    ]

__SOURCE_DIRECTORY__ </> "WebSharper.Vsix/source.extension.vsixmanifest.in" |> replacesInFile [   
    "{vsixversion}", version
]

let dotnetProjReplaces =
    [   
        for p, v in packageVersions do
            yield 
                sprintf "Include=\"%s\"" p, 
                sprintf "Include=\"%s\" Version=\"%s\"" p v
    ]

Directory.EnumerateFiles(__SOURCE_DIRECTORY__, "*.FSharp.fsproj.in", SearchOption.AllDirectories)
|> Seq.iter (replacesInFile dotnetProjReplaces)

Directory.EnumerateFiles(__SOURCE_DIRECTORY__, "*.CSharp.csproj.in", SearchOption.AllDirectories)
|> Seq.iter (replacesInFile dotnetProjReplaces)

let wsRef = """    <PackageReference Include="WebSharper" """

let ancNugetRef =
    """    $if$ ($visualstudioversion$ < 16.0)<PackageReference Include="Microsoft.AspNetCore.All" Version="2.0.8" />
    $endif$<PackageReference Include="WebSharper" """

Directory.EnumerateDirectories(__SOURCE_DIRECTORY__ </> "NetCore")
|> Seq.iter (fun ncPath ->
    match Path.GetFileName(ncPath).Split('-') with
    | [| name; lang |] ->
        let vcPath = __SOURCE_DIRECTORY__ </> lang </> (name + "-NetCore")
        Directory.EnumerateFiles(ncPath, "*.*", SearchOption.AllDirectories)
        |> Seq.iter (fun f ->
            if not (f.Contains("\\bin") || f.Contains("\\obj") || f.Contains("\\.template.config") || f.EndsWith(".in") || f.EndsWith(".user")) then
                let fn =
                    if f.EndsWith("proj") then 
                        "ProjectTemplate." + (if lang = "CSharp" then "csproj" else "fsproj")    
                    else f
                let copyTo =
                    vcPath </> Fake.IO.Path.toRelativeFrom ncPath fn
                printfn "Copied: %s -> %s" f copyTo
                Directory.CreateDirectory(Path.GetDirectoryName(copyTo)) |> ignore
                let res = 
                    File.ReadAllText(f)
                        .Replace(sprintf "WebSharper.%s.%s" name lang, "$safeprojectname$")
                        .Replace("IWebHostEnvironment", "$if$ ($visualstudioversion$ >= 16.0)IWebHostEnvironment$else$IHostingEnvironment$endif$")
                let res =
                    if res.Contains("netcoreapp3.1") then
                        res
                            .Replace("netcoreapp3.1", "$aspnetcoreversion$")
                            .Replace(wsRef, ancNugetRef)
                    else
                        res
                
                File.WriteAllText(copyTo, res)
        )
    | _ -> ()
)

Shell.Exec(
    "tools/nuget/NuGet.exe",
    sprintf "pack -Version %s -OutputDirectory build WebSharper.Templates.nuspec" version
)

match environVarOrNone "NugetPublishUrl", environVarOrNone "NugetApiKey" with
| Some nugetPublishUrl, Some nugetApiKey ->
    tracefn "[NUGET] Publishing to %s" nugetPublishUrl 
    Paket.Push <| fun p ->
        { p with
            PublishUrl = nugetPublishUrl
            ApiKey = nugetApiKey
            WorkingDir = "build"
        }
| _ -> traceError "[NUGET] Not publishing: NugetPublishUrl and/or NugetApiKey are not set"
