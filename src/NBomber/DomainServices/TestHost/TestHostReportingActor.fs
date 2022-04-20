module internal NBomber.DomainServices.TestHost.TestHostReportingActor

open System
open System.Threading.Tasks
open System.Threading.Tasks.Dataflow

open FsToolkit.ErrorHandling
open FsToolkit.ErrorHandling.Operator.Task

open NBomber
open NBomber.Contracts.Stats
open NBomber.Extensions.Internal
open NBomber.Domain.LoadTimeLine
open NBomber.Domain.Stats.Statistics
open NBomber.Domain.Concurrency.Scheduler.ScenarioScheduler
open NBomber.Infra.Dependency

let saveRealtimeScenarioStats (dep: IGlobalDependency) (stats: ScenarioStats[]) = backgroundTask {
    for sink in dep.ReportingSinks do
        try
            do! sink.SaveRealtimeStats(stats)
        with
        | ex -> dep.Logger.Warning(ex, "Reporting sink: {SinkName} failed to save scenario stats", sink.SinkName)
}

let getRealtimeScenarioStats (schedulers: ScenarioScheduler list) (executionTime: TimeSpan) =
    schedulers
    |> List.filter(fun x  -> x.Working = true)
    |> List.map(fun x -> x.GetRealtimeStats executionTime)
    |> Task.WhenAll

let getFinalScenarioStats (schedulers: ScenarioScheduler list) =
    schedulers
    |> List.map(fun x -> x.GetFinalStats())
    |> Task.WhenAll

let getPluginStats (dep: IGlobalDependency) (stats: NodeStats) = backgroundTask {
    try
        let pluginStatusesTask =
            dep.WorkerPlugins
            |> List.map(fun plugin -> plugin.GetStats stats)
            |> Task.WhenAll

        let! finishedTask = Task.WhenAny(pluginStatusesTask, Task.Delay Constants.GetPluginStatsTimeout)
        if finishedTask.Id = pluginStatusesTask.Id then return pluginStatusesTask.Result
        else
            dep.Logger.Error("Getting plugin stats failed with the timeout error")
            return Array.empty
    with
    | ex ->
        dep.Logger.Error(ex, "Getting plugin stats failed with the following error")
        return Array.empty
}

let getFinalStats (dep: IGlobalDependency)
                  (schedulers: ScenarioScheduler list)
                  (testInfo: TestInfo)
                  (nodeInfo: NodeInfo) = backgroundTask {

    let! scenarioStats = getFinalScenarioStats schedulers

    if Array.isEmpty scenarioStats then return None
    else
        let nodeStats = NodeStats.create testInfo nodeInfo scenarioStats
        let! pluginStats = getPluginStats dep nodeStats
        return Some { nodeStats with PluginStats = pluginStats }
}

type ActorMessage =
    | SaveRealtimeStats  of executionTime:TimeSpan
    | GetTimeLineHistory of TaskCompletionSource<TimeLineHistoryRecord list>
    | GetFinalStats      of TaskCompletionSource<NodeStats> * NodeInfo

type TestHostReportingActor(dep: IGlobalDependency, schedulers: ScenarioScheduler list, testInfo: TestInfo) =

    let saveRealtimeStats = saveRealtimeScenarioStats dep
    let getRealtimeStats = getRealtimeScenarioStats schedulers
    let getFinalStats = getFinalStats dep schedulers testInfo

    let mutable _currentHistory = List.empty<TimeLineHistoryRecord>

    let getAndSaveRealtimeStats (executionTime, history) = backgroundTask {
        let! scnStats = getRealtimeStats executionTime
        if Array.isEmpty scnStats then return history
        else
            do! scnStats |> Array.map(ScenarioStats.round) |> saveRealtimeStats
            let historyRecord = TimeLineHistoryRecord.create scnStats
            return historyRecord :: history
    }

    let _actor = ActionBlock(fun msg ->
        backgroundTask {
            try
                match msg with
                | SaveRealtimeStats executionTime ->
                    let! newHistory = getAndSaveRealtimeStats(executionTime, _currentHistory)
                    _currentHistory <- newHistory

                | GetTimeLineHistory reply ->
                    _currentHistory |> TimeLineHistory.filterRealtime |> reply.TrySetResult |> ignore

                | GetFinalStats (reply, nodeInfo) ->
                    nodeInfo
                    |> getFinalStats
                    |> TaskOption.map(NodeStats.round >> reply.TrySetResult)
                    |> Task.WaitAll
            with
            | ex -> dep.Logger.Error("TestHostReporting actor failed: {0}", ex.ToString())
        }
        :> Task
    )

    member _.Publish(msg) = _actor.Post(msg) |> ignore

    member _.GetTimeLineHistory() =
        let tcs = TaskCompletionSource<TimeLineHistoryRecord list>()
        GetTimeLineHistory(tcs) |> _actor.Post |> ignore
        tcs.Task

    member _.GetFinalStats(nodeInfo) =
        let tcs = TaskCompletionSource<NodeStats>()
        GetFinalStats(tcs, nodeInfo) |> _actor.Post |> ignore
        tcs.Task
