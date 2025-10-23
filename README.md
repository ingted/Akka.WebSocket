# Akka.WebSocket

Akka.WebSocket is a tiny bridge that wires a running [Akka.NET](https://getakka.net/) actor system to a [Suave](https://suave.io/) WebSocket endpoint. It targets both `net9.0` and `netstandard2.0`, so the same package works for .NET 9 applications and legacy .NET Framework hosts.

The library exposes a single entry point: start Suave, hand it an `ActorSystem` plus the actor that should receive WebSocket JSON/text payloads, and the bridge will keep forwarding messages asynchronously. It also provides a REST-style health check that round-trips through your actor, so downstream load balancers can observe the same infrastructure used by WebSocket clients.

## Features

- **Actor-first design** – no `Ask`/blocking calls; messages are `Tell`ed to the supplied actor.
- **Health probe support** – `/health` sends `"healthcheck"` to the handler actor and returns its response.
- **Multi-target build** – ships as a single package for `net9.0` and `netstandard2.0`.
- **Graceful shutdown** – disposing the returned `ServerHandle` stops Suave and cancels the WebSocket loop.

## Getting Started

Install the package from NuGet (package id pending):

```bash
dotnet add package Akka.WebSocket
```

Create an actor that understands your payloads (and optionally the `"healthcheck"` message) and start the bridge:

```fsharp
open System
open Akka.Actor
open Akka.WebSocketBridge

type EchoActor() =
    inherit UntypedActor()
    override _.OnReceive msg =
        match msg with
        | :? string as text when text = "healthcheck" ->
            base.Sender.Tell("ok", base.Self)
        | :? string as text ->
            printfn "client said: %s" text
        | _ -> ()

let system = ActorSystem.Create("WebSocketCluster")
let echo = system.ActorOf(Props.Create<EchoActor>(), "ws-echo")

let server =
    WebSocketServer.start
        system
        "ws"
        None
        (Choice2Of2 ("0.0.0.0", 8080))
        (fun _ -> Some echo)

printfn "Listening on ws://0.0.0.0:8080/ws"
Console.ReadLine() |> ignore
server.Stop()
system.Terminate() |> Async.AwaitTask |> Async.RunSynchronously
```

Connect with any WebSocket client (browser, PowerShell, `wscat`, etc.) and send UTF-8 text frames; they appear in your actor. Pings are answered automatically, and fragmented/binary frames close the connection.

## Demo Host

The repository contains `Program.fs`, a sample console host that starts:

- an Akka.NET cluster seed node (defaults: `127.0.0.1:4053`),
- a single `EchoActor`, and
- the WebSocket bridge bound to `ws://0.0.0.0:8080/ws`.

Run it with:

```bash
dotnet run -f net9.0 --project akka.websocket.fsproj \
    -- 0.0.0.0 8080 127.0.0.1 4053 "akka.tcp://WebSocketCluster@127.0.0.1:4053"
```

Arguments (all optional):

| Position | Meaning              | Default      |
|----------|----------------------|--------------|
| 0        | HTTP bind address    | `0.0.0.0`    |
| 1        | HTTP port            | `8080`       |
| 2        | Cluster hostname     | `127.0.0.1`  |
| 3        | Cluster port         | `4053`       |
| 4        | Seed node list       | self address |

### Health Check

The demo actor replies `"ok"` to `"healthcheck"`. The `/health` endpoint calls into that actor, so you can verify end-to-end readiness:

```bash
curl http://localhost:8080/health
# => ok
```

If the actor is unavailable or throws, the endpoint returns an HTTP 500 with the failure message.

## Building and Packing

Restore and build:

```bash
dotnet restore
dotnet build
```

Create a NuGet package (produces both target frameworks):

```bash
dotnet pack -c Release -o nupkgs
```

Publish the resulting `.nupkg` to NuGet with your preferred workflow (`dotnet nuget push`, GitHub Actions, etc.).

## License

This project is licensed under the terms of the included `LICENSE` file.
