﻿module BuildHistoryCharts

open System
open RestClient
open Newtonsoft.Json.Linq
open Serializers

// -------------------------------------------
// Common Types and Functions
// -------------------------------------------

type BuildStatus =
    | Success
    | Failed
    | Cancelled
    | Pending
    | Unkown

type Build =
    {
        Id              : int
        BuildNumber     : int
        TimeTaken       : TimeSpan
        Status          : BuildStatus
        Branch          : string
        FromPullRequest : bool
    }

let pullRequestFilter   (inclFromPullRequest : bool)
                        (build  : Build) =
    inclFromPullRequest || not build.FromPullRequest

let getTimeTaken (started   : Nullable<DateTime>)
                 (finished  : Nullable<DateTime>) =
    match started.HasValue with
    | true ->
        match finished.HasValue with
        | true  -> finished.Value - started.Value
        | false -> TimeSpan.Zero
    | false     -> TimeSpan.Zero

// -------------------------------------------
// Build Metrics
// -------------------------------------------

module BuildMetrics =
    
    let longestBuildTime (builds : Build list) =
        builds
        |> List.maxBy (fun x -> x.TimeTaken.TotalMilliseconds)
        |> fun x -> x.TimeTaken

    let shortestBuildTime (builds : Build list) =
        builds
        |> List.minBy (fun x -> x.TimeTaken.TotalMilliseconds)
        |> fun x -> x.TimeTaken

    let averageBuildTime (builds : Build list) =
        builds
        |> List.averageBy (fun x -> x.TimeTaken.TotalMilliseconds)
        |> TimeSpan.FromMilliseconds

// -------------------------------------------
// AppVeyor
// -------------------------------------------

module AppVeyor =

    let parseToJArray (json : string) =
        match json with
        | null | "" -> None
        | json ->
            let obj = deserializeJson json :?> JObject
            Some <| obj.Value<JArray> "builds"

    let parseStatus (status : string) =
        match status with
        | "success"             -> Success
        | "failed"              -> Failed
        | "cancelled"           -> Cancelled
        | "queued" | "running"  -> Pending
        | _                     -> Unkown

    let isPullRequest (pullRequestId : string) =
        pullRequestId <> null

    let convertToBuilds (items : JArray option) =
        match items with
        | None       -> []
        | Some items ->
            items 
            |> Seq.map (fun x ->
                let started  = x.Value<Nullable<DateTime>> "started"
                let finished = x.Value<Nullable<DateTime>> "finished"
                {
                    Id              = x.Value<int>    "buildId"
                    BuildNumber     = x.Value<int>    "buildNumber"
                    Status          = x.Value<string> "status"        |> parseStatus
                    Branch          = x.Value<string> "branch"
                    FromPullRequest = x.Value<string> "pullRequestId" |> isPullRequest
                    TimeTaken       = getTimeTaken started finished
                })
            |> Seq.toList

    let getBuilds   (account             : string) 
                    (project             : string) 
                    (buildCount          : int) 
                    (branch              : string option) 
                    (inclFromPullRequest : bool) = 
        async {
            let additionalFilter =
                match branch with
                | Some b -> sprintf "&branch=%s" b
                | None   -> ""
                
            let url = 
                sprintf "https://ci.appveyor.com/api/projects/%s/%s/history?recordsNumber=%d%s" 
                    account project (5 * buildCount) additionalFilter

            let! json = getAsync url Json

            return json
                |> parseToJArray
                |> convertToBuilds
                |> List.filter (pullRequestFilter inclFromPullRequest)
                |> List.truncate buildCount
        }

// -------------------------------------------
// TravisCI
// -------------------------------------------

module TravisCI =

    let parseToJArray (json : string) =
        match json with
        | null | "" -> None
        | json      -> Some (deserializeJson json :?> JArray)

    let parseStatus (state  : string)
                    (result : Nullable<int>) =
        match state with
        | "finished" -> 
            match result.Value with
            | 0     -> Success
            | _     -> Failed
        | "started" -> Pending
        | _         -> Unkown

    let isPullRequest eventType = eventType = "pull_request"

    let convertToBuilds (items : JArray option) =
        match items with
        | None -> []
        | Some items ->
            items 
            |> Seq.map (fun x ->
                let started  = x.Value<Nullable<DateTime>> "started_at"
                let finished = x.Value<Nullable<DateTime>> "finished_at"
                let state    = x.Value<string>             "state"
                let result   = x.Value<Nullable<int>>      "result"
                {
                    Id              = x.Value<int>    "id"
                    BuildNumber     = x.Value<int>    "number"
                    Branch          = x.Value<string> "branch"
                    FromPullRequest = x.Value<string> "event_type" |> isPullRequest
                    TimeTaken       = getTimeTaken  started finished
                    Status          = parseStatus   state   result
                })
            |> Seq.toList

    let getBatchOfBuilds (account          : string) 
                         (project          : string)
                         (branch           : string)
                         (inclPullRequest  : bool)
                         (afterBuildNumber : int option) =
        async {
            let additionalQuery =
                match afterBuildNumber with
                | Some x -> sprintf "?after_number=%i" x
                | None   -> "" 
            let url = sprintf "https://api.travis-ci.org/repos/%s/%s/builds%s" account project additionalQuery
            let! json = getAsync url Json
            return
                json
                |> parseToJArray
                |> convertToBuilds
        }

//    let test (account          : string) 
//             (project          : string)
//             (branch           : string)
//             (inclPullRequest  : bool)
//             (afterBuildNumber : int option) =
//        async {
//            let! builds = getBatchOfBuilds account project branch inclPullRequest afterBuildNumber
//
//        }
        

    let getBuilds   (account             : string) 
                    (project             : string) 
                    (buildCount          : int) 
                    (branch              : string option) 
                    (inclFromPullRequest : bool) = 
        async {
            let additionalFilter =
                match branch with
                | Some b -> sprintf "&branch=%s" b
                | None   -> ""

            // Pulling a bit more builds in case some get excluded by the pull request filter
            let recordsNumber = 5 * buildCount

            let url = sprintf "https://ci.appveyor.com/api/projects/%s/%s/history?recordsNumber=%d%s" account project recordsNumber additionalFilter
            
            let! json = getAsync url Json

            return json
                |> parseToJArray
                |> convertToBuilds
                |> List.filter (pullRequestFilter inclFromPullRequest)
                |> List.truncate buildCount
        }

// -------------------------------------------
// CircleCI
// -------------------------------------------

// ToDo