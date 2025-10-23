namespace Akka.WebSocketBridge

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Event
open Akka.Cluster
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Suave.ServerErrors

/// Handle returned when the server is started; dispose or call Stop to shut it down.
type ServerHandle internal (cts: CancellationTokenSource) =
    member _.Stop() =
        if not cts.IsCancellationRequested then
            cts.Cancel()

    interface IDisposable with
        member this.Dispose() = this.Stop()

type ReplyActor<'T>(system: ActorSystem, tcs: TaskCompletionSource<'T>) as this =
    inherit UntypedActor()

    let selfRef = this.Self

    override _.OnReceive message =
        match message with
        | :? 'T as payload ->
            if tcs.TrySetResult payload then
                system.Stop selfRef

        | :? Akka.Actor.Status.Failure as failure ->
            let cause : Exception = failure.Cause
            if tcs.TrySetException(cause) then
                system.Stop selfRef

        | _ -> ()

    override _.PostStop() =
        if not tcs.Task.IsCompleted then
            tcs.TrySetCanceled() |> ignore

type EchoActor(system: ActorSystem, nodeAddress: string) as this =
    inherit UntypedActor()

    let log = system.Log

    override _.PreStart() =
        log.Info("EchoActor starting on {0}", nodeAddress)

    override _.PostStop() =
        log.Info("EchoActor stopped.")

    override _.OnReceive message =
        match message with
        | :? string as msg when String.Equals(msg, "healthcheck", StringComparison.OrdinalIgnoreCase) ->
            printfn "checking healthness by actor"
            this.Sender.Tell("ok", this.Self)

        | :? string as msg ->
            log.Info("EchoActor received payload: {0}", msg)

        | _ -> ()

module WebSocketServer =

    let log (actorSystem: ActorSystem) =
        Logging.GetLogger(actorSystem, "Akka.WebSocketBridge")

    type SocketPayload = byte[]

    let emptyPayload : SocketPayload = Array.empty<byte>

    let decodeUtf8 (data: SocketPayload) =
        Encoding.UTF8.GetString data

    let sendWithReply<'TResponse>
        (system: ActorSystem)
        (target: IActorRef)
        (messageFactory: IActorRef -> obj)
        : Async<'TResponse> =
        async {
            let tcs = TaskCompletionSource<'TResponse>()
            let adapter =
                system.ActorOf(
                    Props.Create(typeof<ReplyActor<'TResponse>>, [| system :> obj; tcs :> obj |])
                )

            let message = messageFactory adapter
            target.Tell(message, adapter)

            try
                return! tcs.Task |> Async.AwaitTask
            finally
                system.Stop(adapter)
        }

    let inline sendFrame (ws: WebSocket) opcode (data: SocketPayload) =
        let segment = ArraySegment<byte>(data, 0, data.Length)
        ws.send opcode segment true

    let websocketLoop
        (actorSystem: ActorSystem)
        (handleActorGetter: WebSocket option -> IActorRef option)
        (ws: WebSocket)
        : SocketOp<unit> =
        let logger = log actorSystem

        match handleActorGetter (Some ws) with
        | None ->
            socket {
                logger.Error("No handler actor available for WebSocket connection; closing client.")
                do! sendFrame ws Close emptyPayload
                return ()
            }
        | Some handler ->
            let rec loop () =
                socket {
                    let! msg = ws.read()

                    match msg with
                    | (Text, (data: SocketPayload), true) ->
                        let payload = decodeUtf8 data
                        logger.Debug("Forwarding WebSocket payload: {0}", payload)
                        handler.Tell(payload, ActorRefs.NoSender)
                        return! loop ()

                    | (Text, d, false) ->
                        logger.Warning(sprintf "Received fragmented WebSocket message %A. Closing connection." d)
                        do! sendFrame ws Close emptyPayload
                        return ()

                    | (Close, _, _) ->
                        logger.Info("Client requested WebSocket close.")
                        do! sendFrame ws Close emptyPayload
                        return ()

                    | (Ping, (data: SocketPayload), _) ->
                        do! sendFrame ws Pong data
                        return! loop ()

                    | (Binary, _, _)
                    | (Pong, _, _)
                    | (Continuation, _, _)
                    | (Reserved, _, _) ->
                        // Not supporting binary or continuation frames; stay silent.
                        return! loop ()
                }

            loop ()

    /// Start a Suave WebSocket endpoint that forwards JSON text frames to the supplied actor.
    let start
        (actorSystem: ActorSystem)
        (wsName: string)
        (defaultWebpartOpt: (IActorRef option -> WebPart list) option)
        configChoice
        (handleActorGetter: WebSocket option -> IActorRef option)
        : ServerHandle =

        if isNull (box actorSystem) then
            nullArg (nameof actorSystem)

        if isNull (box handleActorGetter) then
            nullArg (nameof handleActorGetter)

        let wsEndpoint =
            let socketLoop = websocketLoop actorSystem handleActorGetter
            handShake (fun ws _ -> socketLoop ws)

        let healthRoute : WebPart =
            fun ctx ->
                async {
                    match handleActorGetter None with
                    | None ->
                        return! SERVICE_UNAVAILABLE "handler unavailable" ctx
                    | Some handler ->
                        try
                            let! reply =
                                sendWithReply<string> actorSystem handler (fun replyTo -> box "healthcheck")
                            return! OK reply ctx
                        with ex ->
                            return! INTERNAL_ERROR ex.Message ctx
                }

        let webApp =
            match defaultWebpartOpt with
            | None -> []
            | Some lGen -> lGen (handleActorGetter None)
            |> List.append [
                path $"/{wsName}" >=> wsEndpoint
                path "/health" >=> healthRoute
            ]
            |> choose

        let config =
            match configChoice with
            | Choice1Of2 c -> c
            | Choice2Of2 (host, port) ->
                { defaultConfig with
                    bindings = [ HttpBinding.createSimple HTTP host port ]
                    homeFolder = None }

        let listening, server = startWebServerAsync config webApp
        let cts = new CancellationTokenSource()

        Async.Start(server, cancellationToken = cts.Token)

        listening
        |> Async.Catch
        |> Async.RunSynchronously
        |> function
            | Choice1Of2 _ -> ()
            | Choice2Of2 ex ->
                cts.Cancel()
                raise ex
        new ServerHandle(cts)

module Program =

    let parseInt (fallback: int) (value: string) =
        let maxPort = int UInt16.MaxValue
        match Int32.TryParse value with
        | true, v when v > 0 && v <= maxPort -> v
        | _ -> fallback

    let pickArg (args: string[]) index fallback =
        if args.Length > index then args.[index] else fallback

    let demo args =
        let httpHost = pickArg args 0 "0.0.0.0"
        let httpPort = pickArg args 1 "8080" |> parseInt 8080
        let clusterHost = pickArg args 2 "127.0.0.1"
        let clusterPort = pickArg args 3 "4053" |> parseInt 4053

        let seedNodes =
            if args.Length > 4 then
                args.[4]
                    .Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
            else
                [| sprintf "akka.tcp://WebSocketCluster@%s:%d" clusterHost clusterPort |]

        let seedNodesLiteral =
            seedNodes
            |> Array.map (fun node -> sprintf "\"%s\"" node)
            |> String.concat ", "

        let hocon =
            $"""
akka {{
  loglevel = "INFO"
  stdout-loglevel = "INFO"
  actor.provider = "cluster"

  remote.dot-netty.tcp {{
    hostname = "{clusterHost}"
    public-hostname = "{clusterHost}"
    port = {clusterPort}
  }}

  cluster {{
    seed-nodes = [{seedNodesLiteral}]
  }}
}}
"""

        let config = ConfigurationFactory.ParseString hocon
        let actorSystem = ActorSystem.Create("WebSocketCluster", config)
        let log = actorSystem.Log
        let cluster = Cluster.Get(actorSystem)
        let nodeAddress = cluster.SelfAddress.ToString()
        log.Info("Cluster seed node configured at {0}", nodeAddress)

        let echoProps = Props.Create<EchoActor>(actorSystem, nodeAddress)
        let echoActor = actorSystem.ActorOf(echoProps, "ws-echo")
        log.Info("Echo actor spawned at {0}", echoActor.Path.ToStringWithUid())

        let echoActorGetter (_: WebSocket option) =
            Some echoActor

        let server = WebSocketServer.start actorSystem "ws" None (Choice2Of2 (httpHost, httpPort)) echoActorGetter
        log.Info("Suave WebSocket bridge listening on ws://{0}:{1}/ws", httpHost, httpPort)

        let shutdownLock = obj ()
        let mutable stopped = false

        let shutdown () =
            lock shutdownLock (fun () ->
                if not stopped then
                    stopped <- true
                    log.Info("Shutting down WebSocket bridge and actor system...")
                    server.Stop()
                    actorSystem.Terminate()
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    actorSystem.WhenTerminated.Wait()
                    log.Info("Termination complete.")
            )

        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            shutdown ()
        )

        printfn "Seed nodes: %s" (seedNodes |> String.concat ", ")
        printfn "Press ENTER to stop..."
        Console.ReadLine() |> ignore

        shutdown ()
        

    [<EntryPoint>]
    let main args =
        demo args
        0
