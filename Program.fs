namespace Akka.WebSocketBridge

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open Newtonsoft.Json
open Newtonsoft.Json.Linq
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
        (handleActorGetter: ActorSystem -> WebSocket option -> IActorRef option)
        (ws: WebSocket)
        : SocketOp<unit> =
        let logger = log actorSystem

        match handleActorGetter actorSystem (Some ws) with
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

                        let handledAsJson =
                            try
                                match JToken.Parse(payload) with
                                | :? JArray as arr ->
                                    let elements = arr.Children()
                                    if elements |> Seq.forall (fun element -> element.Type = JTokenType.String) then
                                        let values =
                                            elements
                                            |> Seq.map (fun element -> element.ToObject<string>())
                                            |> Seq.toArray
                                        handler.Tell(values, ActorRefs.NoSender)
                                        true
                                    else
                                        false
                                | :? JValue as value when value.Type = JTokenType.String ->
                                    handler.Tell(value.ToObject<string>(), ActorRefs.NoSender)
                                    true
                                | _ -> false
                            with
                            | :? JsonReaderException -> false

                        if not handledAsJson then
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
        (handleActorGetter: ActorSystem -> WebSocket option -> IActorRef option)
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
                    match handleActorGetter actorSystem None with
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
            | Some lGen -> lGen (handleActorGetter actorSystem None)
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

module SimpleKiller =

    open System
    open System.Diagnostics
    open System.Runtime.InteropServices

    //[<StructLayout(LayoutKind.Sequential)>]
    type STARTUPINFOEX() =
        member val StartupInfo = new ProcessStartInfo() with get, set

    [<Flags>]
    type ProcessCreationFlags =
        | EXTENDED_STARTUPINFO_PRESENT = 0x00080000
        | CREATE_NO_WINDOW = 0x08000000

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateProcess(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        ProcessCreationFlags dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        IntPtr lpStartupInfo,
        IntPtr lpProcessInformation
    )

    let startWithoutHandleInheritance (encodedScript: string) =
        let cmd = sprintf "powershell.exe -NoLogo -NoProfile -EncodedCommand %s" encodedScript
        let ok =
            CreateProcess(
                null,
                cmd,
                IntPtr.Zero,
                IntPtr.Zero,
                false, // ❗ 不繼承任何 handle
                ProcessCreationFlags.CREATE_NO_WINDOW,
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                IntPtr.Zero
            )
        if not ok then
            let err = Marshal.GetLastWin32Error()
            failwithf "CreateProcess failed: %d" err
        else
            printfn "PowerShell started without inherited handles."


    type KillActor(system: ActorSystem, nodeAddress: string, wsOpt:WebSocket option) as this =
        inherit UntypedActor()

        let log = system.Log

        let escapeForSingleQuotedLiteral (value: string) =
            if isNull value then
                String.Empty
            else
                value.Replace("'", "''")

        let startMonitoringProcess (filePath: string) (scriptText: string) =
            let safeFilePath = if isNull filePath then String.Empty else filePath
            let safeScriptText = if isNull scriptText then String.Empty else scriptText

            if String.IsNullOrWhiteSpace safeFilePath then
                log.Warning("KillActor received kill command without a target file; skipping PowerShell monitor.")
            else
                try
                    let fileLiteral = $"'{escapeForSingleQuotedLiteral safeFilePath}'"
                    let scriptBase64 =
                        if String.IsNullOrWhiteSpace safeScriptText then
                            String.Empty
                        else
                            Convert.ToBase64String(Encoding.Unicode.GetBytes safeScriptText)
                    let scriptLiteral = $"'{escapeForSingleQuotedLiteral scriptBase64}'"

                    let psBuilder = StringBuilder()
                    psBuilder.AppendLine("$ErrorActionPreference = 'Stop'") |> ignore
                    psBuilder.AppendLine($"$targetPath = {fileLiteral}") |> ignore
                    psBuilder.AppendLine($"$scriptTextBase64 = {scriptLiteral}") |> ignore
                    psBuilder.AppendLine("function Get-FileSignature {") |> ignore
                    psBuilder.AppendLine("    param([string]$Path)") |> ignore
                    psBuilder.AppendLine("    if (-not (Test-Path -LiteralPath $Path)) {") |> ignore
                    psBuilder.AppendLine("        return $null") |> ignore
                    psBuilder.AppendLine("    }") |> ignore
                    psBuilder.AppendLine("    $item = Get-Item -LiteralPath $Path") |> ignore
                    psBuilder.AppendLine("    $version = $null") |> ignore
                    psBuilder.AppendLine("    try {") |> ignore
                    psBuilder.AppendLine("        $version = $item.VersionInfo.FileVersion") |> ignore
                    psBuilder.AppendLine("    } catch {") |> ignore
                    psBuilder.AppendLine("        $version = $null") |> ignore
                    psBuilder.AppendLine("    }") |> ignore
                    psBuilder.AppendLine("    [pscustomobject]@{") |> ignore
                    psBuilder.AppendLine("        Version = $version") |> ignore
                    psBuilder.AppendLine("        Created = $item.LastWriteTime") |> ignore
                    psBuilder.AppendLine("    }") |> ignore
                    psBuilder.AppendLine("}") |> ignore
                    psBuilder.AppendLine("$initial = Get-FileSignature -Path $targetPath") |> ignore
                    psBuilder.AppendLine("while ($true) {") |> ignore
                    psBuilder.AppendLine("    Start-Sleep -Milliseconds 1000") |> ignore
                    psBuilder.AppendLine("    $current = Get-FileSignature -Path $targetPath") |> ignore
                    psBuilder.AppendLine("    if ($null -eq $initial) {") |> ignore
                    psBuilder.AppendLine("        if ($null -ne $current) { break }") |> ignore
                    psBuilder.AppendLine("    } elseif ($null -ne $current) {") |> ignore
                    psBuilder.AppendLine("        if (($current.Version -ne $initial.Version) -or ($current.Created -ne $initial.Created)) {") |> ignore
                    psBuilder.AppendLine("            break") |> ignore
                    psBuilder.AppendLine("        }") |> ignore
                    psBuilder.AppendLine("    }") |> ignore
                    psBuilder.AppendLine("}") |> ignore
                    psBuilder.AppendLine("if (-not [string]::IsNullOrWhiteSpace($scriptTextBase64)) {") |> ignore
                    psBuilder.AppendLine("    $scriptText = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String($scriptTextBase64))") |> ignore
                    psBuilder.AppendLine("    if (-not [string]::IsNullOrWhiteSpace($scriptText)) {") |> ignore
                    psBuilder.AppendLine("        $scriptBlock = [scriptblock]::Create($scriptText)") |> ignore
                    psBuilder.AppendLine("        & $scriptBlock") |> ignore
                    psBuilder.AppendLine("    }") |> ignore
                    psBuilder.AppendLine("}") |> ignore

                    let script = psBuilder.ToString()

                    //IO.File.WriteAllText("C:\\killactor_monitor.ps1", script)

                    let encodedScript =
                        script
                        //"write-host 'hi'"
                        |> Encoding.Unicode.GetBytes
                        |> Convert.ToBase64String

                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- "powershell.exe"
                    //startInfo.Arguments <- "-NoExit -NoLogo -NoProfile -EncodedCommand " + encodedScript
                    startInfo.Arguments <- "-NoLogo -NoProfile -EncodedCommand " + encodedScript
                    startInfo.CreateNoWindow <- true
                    startInfo.WindowStyle <- ProcessWindowStyle.Hidden

                    startInfo.UseShellExecute <- true
                    //startInfo.RedirectStandardInput <- true  
                    //startInfo.RedirectStandardOutput <- true
                    //startInfo.RedirectStandardError <- true


                    let proc = Process.Start(startInfo)

                    //startWithoutHandleInheritance encodedScript

                    //log.Info("KillActor started monitoring PowerShell process '{1}'.", safeFilePath)

                    match proc with
                    | null ->
                        log.Warning("KillActor could not start monitoring PowerShell process for '{0}'.", safeFilePath)
                    | proc ->
                        log.Info("KillActor started monitoring PowerShell process (PID {0}) for '{1}'.", proc.Id, safeFilePath)
                with ex ->
                    log.Warning("KillActor failed to launch monitoring PowerShell process for '{0}'. Cause: {1}", safeFilePath, ex.Message)
        static member val locker: obj =  obj() with get
        static member val iaref: IActorRef option = None with get, set

        override _.PreStart() =
            log.Info("KillActor starting on {0}", nodeAddress)

        override _.PostStop() =
            log.Info("KillActor stopped.")

        override _.OnReceive message =
            match message with
            | :? (string[]) as payload ->
                if payload.Length = 0 then
                    log.Warning("KillActor received empty string array payload.")
                else
                    let command = payload.[0]
                    if String.Equals(command, "healthcheck", StringComparison.OrdinalIgnoreCase) then
                        printfn "checking healthness by actor"
                        this.Sender.Tell("ok", this.Self)
                    elif String.Equals(command, "kill", StringComparison.OrdinalIgnoreCase) then
                        let filePath = if payload.Length > 1 then payload.[1] else null
                        let scriptText = if payload.Length > 2 then payload.[2] else null
                        startMonitoringProcess filePath scriptText 
                        log.Info("KillActor killing now.")
                        Environment.Exit(0)
                    else
                        log.Info("KillActor received string[] payload for command: {0}", command)

            | :? string as msg when String.Equals(msg, "healthcheck", StringComparison.OrdinalIgnoreCase) ->
                printfn "checking healthness by actor"
                if wsOpt.IsNone then
                    this.Sender.Tell("ok", this.Self)
                else
                    wsOpt.Value.send Text (ArraySegment<byte>(Encoding.UTF8.GetBytes "ok")) true
                    |> Async.Ignore
                    |> Async.Start

            | :? string as msg when String.Equals(msg, "kill", StringComparison.OrdinalIgnoreCase) ->
                log.Info("KillActor killing now (no automation payload).")
                Environment.Exit(0)

            | :? string as msg ->
                log.Info("KillActor received payload: {0}", msg)

            | msg -> 
                log.Info(sprintf "KillActor received payload: %A" msg)

    let simpleKillerFun nodeAddress (actorSystem:ActorSystem) (wsOpt:WebSocket option) =
        lock KillActor.locker (fun _ ->
            if KillActor.iaref.IsSome then
                KillActor.iaref
            else
                let killProps = Props.Create<KillActor>(actorSystem, nodeAddress, wsOpt)
                let killActor = actorSystem.ActorOf(killProps, "ws-kill")
                KillActor.iaref <- Some killActor
                Some killActor
        )

module Program =
    open System
    open System.IO
    open System.Reflection
    //let exeDir = AppDomain.CurrentDomain.BaseDirectory

    //let dll = Directory.GetFiles(exeDir, "MergedSB.dll")[0]

    //try
    //    Assembly.LoadFile(dll) |> ignore
    //    printfn "Loaded: %s" dll
    //with ex ->
    //        printfn "Failed to load %s: %s" dll ex.Message


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

        let echoActorGetter (asys:ActorSystem) (_: WebSocket option) =
            Some echoActor

        let server = WebSocketServer.start actorSystem "ws" None (Choice2Of2 (httpHost, httpPort)) echoActorGetter
        log.Info("Suave WebSocket bridge listening on ws://{0}:{1}/ws", httpHost, httpPort)

        let server2 = WebSocketServer.start actorSystem "ws2" None (Choice2Of2 (httpHost, 60254)) (SimpleKiller.simpleKillerFun httpHost)

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
